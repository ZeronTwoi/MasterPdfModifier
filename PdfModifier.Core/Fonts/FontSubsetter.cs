using System.Buffers.Binary;
using System.Text;

namespace PdfModifier.Core.Fonts;

/// <summary>
/// Shrinks a TrueType font to the characters actually used. Glyph ids are preserved (unused outlines are emptied and
/// layout tables dropped), so nothing has to be renumbered; the character map is rebuilt to list only the used
/// characters, which also keeps text extraction unambiguous when several characters share one glyph.
/// <para>
/// Every subset is named after the characters it holds ("QKXBTA+ArialMT"), as subsets in PDF files are. This is more
/// than a convention here: when PDFium writes a page it tells fonts apart by name alone, so two different cuts of one
/// font with the same name would be drawn with each other's glyphs.
/// </para>
/// </summary>
internal static class FontSubsetter
{
    private static readonly HashSet<string> KeptTables =
        ["head", "hhea", "maxp", "hmtx", "cmap", "cvt ", "fpgm", "prep", "OS/2", "name", "post", "loca", "glyf", "gasp"];

    /// <summary>
    /// Returns the subset, or the original bytes when the font has an unexpected structure.
    /// <paramref name="characters"/> maps each used code point to its glyph id.
    /// </summary>
    public static byte[] Subset(byte[] font, IReadOnlyDictionary<int, ushort> characters)
    {
        try
        {
            return SubsetCore(font, characters) ?? font;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            return font;
        }
    }

    private static byte[]? SubsetCore(byte[] font, IReadOnlyDictionary<int, ushort> characters)
    {
        var glyphIds = characters.Values;
        var span = font.AsSpan();
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var tables = new Dictionary<string, (int Offset, int Length)>();
        for (var i = 0; i < tableCount; i++)
        {
            var entry = 12 + i * 16;
            var tag = Encoding.ASCII.GetString(font, entry, 4);
            tables[tag] = ((int)BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 8)..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 12)..]));
        }

        if (!tables.TryGetValue("head", out var head) || !tables.TryGetValue("maxp", out var maxp) ||
            !tables.TryGetValue("loca", out var loca) || !tables.TryGetValue("glyf", out var glyf))
            return null;

        var glyphCount = BinaryPrimitives.ReadUInt16BigEndian(span[(maxp.Offset + 4)..]);
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(span[(head.Offset + 50)..]) != 0;
        var offsets = new int[glyphCount + 1];
        for (var i = 0; i <= glyphCount; i++)
            offsets[i] = longLoca
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(span[(loca.Offset + i * 4)..])
                : BinaryPrimitives.ReadUInt16BigEndian(span[(loca.Offset + i * 2)..]) * 2;

        // Composite glyphs reference other glyphs, which have to be kept as well.
        var keep = new HashSet<int> { 0 };
        var pending = new Stack<int>(glyphIds.Select(g => (int)g).Append(0));
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (glyph >= glyphCount || (glyph != 0 && !keep.Add(glyph))) continue;
            var data = span.Slice(glyf.Offset + offsets[glyph], offsets[glyph + 1] - offsets[glyph]);
            if (data.Length < 10 || BinaryPrimitives.ReadInt16BigEndian(data) >= 0) continue;
            var pos = 10;
            while (true)
            {
                var flags = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                pending.Push(BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]));
                pos += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                if ((flags & 0x0008) != 0) pos += 2;
                else if ((flags & 0x0040) != 0) pos += 4;
                else if ((flags & 0x0080) != 0) pos += 8;
                if ((flags & 0x0020) == 0) break;
            }
        }

        var newGlyf = new MemoryStream();
        var newLoca = new byte[(glyphCount + 1) * 4];
        for (var glyph = 0; glyph < glyphCount; glyph++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(glyph * 4), (uint)newGlyf.Position);
            if (!keep.Contains(glyph)) continue;
            newGlyf.Write(span.Slice(glyf.Offset + offsets[glyph], offsets[glyph + 1] - offsets[glyph]));
            while (newGlyf.Position % 4 != 0) newGlyf.WriteByte(0);
        }

        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(glyphCount * 4), (uint)newGlyf.Position);

        var newHead = span.Slice(head.Offset, head.Length).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0); // checkSumAdjustment
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), 1); // long loca offsets

        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (tag, (offset, length)) in tables)
        {
            if (!KeptTables.Contains(tag)) continue;
            output[tag] = tag switch
            {
                "head" => newHead,
                "loca" => newLoca,
                "glyf" => newGlyf.ToArray(),
                "post" => StripGlyphNames(span.Slice(offset, length)),
                "cmap" => BuildCharacterMap(characters),
                "name" => Rename(span.Slice(offset, length), SubsetTag(characters)),
                _ => span.Slice(offset, length).ToArray()
            };
        }

        return Assemble(span[..4], output);
    }

    /// <summary>Six letters that stand for a set of characters: the same set always gets the same tag.</summary>
    private static string SubsetTag(IReadOnlyDictionary<int, ushort> characters)
    {
        var hash = 14695981039346656037UL;
        foreach (var (code, glyph) in characters.OrderBy(c => c.Key))
        {
            hash = (hash ^ (uint)code) * 1099511628211UL;
            hash = (hash ^ glyph) * 1099511628211UL;
        }

        var tag = new char[6];
        for (var i = 0; i < tag.Length; i++, hash /= 26) tag[i] = (char)('A' + hash % 26);
        return new string(tag);
    }

    /// <summary>
    /// Rebuilds the 'name' table with the PostScript name prefixed by <paramref name="tag"/>. Family, style and full
    /// name are carried over; everything else (licences, sample texts, translations) is dropped.
    /// </summary>
    private static byte[] Rename(ReadOnlySpan<byte> table, string tag)
    {
        var names = new Dictionary<int, string>();
        if (table.Length >= 6)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
            var storage = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
            for (var i = 0; i < count && 6 + (i + 1) * 12 <= table.Length; i++)
            {
                var record = table[(6 + i * 12)..];
                int platform = BinaryPrimitives.ReadUInt16BigEndian(record), id = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]), offset = storage + BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
                if (id is not (1 or 2 or 4 or 6) || offset + length > table.Length) continue;
                var bytes = table.Slice(offset, length);
                // Windows names are UTF-16; they win over the single-byte Macintosh ones when both are there.
                if (platform == 3) names[id] = Encoding.BigEndianUnicode.GetString(bytes);
                else if (platform == 1) names.TryAdd(id, Encoding.Latin1.GetString(bytes));
            }
        }

        var postScript = names.TryGetValue(6, out var known) && known.Length > 0 ? known
            : names.TryGetValue(1, out var family) ? family.Replace(" ", "")
            : "Font";
        // PostScript names are plain printable ASCII without spaces or brackets.
        names[6] = tag + "+" + new string(postScript.Where(c => c is > ' ' and < (char)127 and not ('(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or '/' or '%')).ToArray());

        var records = new List<(int Platform, int Encoding, int Language, int Id, byte[] Text)>
        {
            (1, 0, 0, 6, Encoding.ASCII.GetBytes(names[6]))
        };
        records.AddRange(names.OrderBy(n => n.Key).Select(n => (3, 1, 0x409, n.Key, Encoding.BigEndianUnicode.GetBytes(n.Value))));

        var header = 6 + records.Count * 12;
        var result = new byte[header + records.Sum(r => r.Text.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)records.Count);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)header);
        var position = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var (platform, encoding, language, id, text) = records[i];
            var record = result.AsSpan(6 + i * 12);
            BinaryPrimitives.WriteUInt16BigEndian(record, (ushort)platform);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], (ushort)encoding);
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], (ushort)language);
            BinaryPrimitives.WriteUInt16BigEndian(record[6..], (ushort)id);
            BinaryPrimitives.WriteUInt16BigEndian(record[8..], (ushort)text.Length);
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], (ushort)position);
            text.CopyTo(result, header + position);
            position += text.Length;
        }

        return result;
    }

    /// <summary>Converts the 'post' table to format 3, which carries no per-glyph names.</summary>
    private static byte[] StripGlyphNames(ReadOnlySpan<byte> post)
    {
        var result = new byte[32];
        post[..Math.Min(32, post.Length)].CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result, 0x00030000);
        return result;
    }

    /// <summary>Writes a 'cmap' with a format 4 subtable (BMP) and a format 12 subtable (all code points).</summary>
    private static byte[] BuildCharacterMap(IReadOnlyDictionary<int, ushort> characters)
    {
        // Runs of consecutive code points whose glyph ids are consecutive too.
        var runs = new List<(int Start, int End, int Glyph)>();
        foreach (var (code, glyph) in characters.Where(c => c.Value != 0).OrderBy(c => c.Key))
        {
            if (runs.Count > 0 && runs[^1] is var last && code == last.End + 1 && glyph == last.Glyph + (code - last.Start))
                runs[^1] = (last.Start, code, last.Glyph);
            else
                runs.Add((code, code, glyph));
        }

        var bmp = runs.Where(r => r.End <= 0xFFFE).ToList();
        var segments = bmp.Count + 1; // plus the mandatory terminating 0xFFFF segment
        var format4 = new byte[16 + segments * 8];
        var entrySelector = (int)Math.Floor(Math.Log2(segments));
        var searchRange = 2 << entrySelector;
        BinaryPrimitives.WriteUInt16BigEndian(format4, 4);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(2), (ushort)format4.Length);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(6), (ushort)(segments * 2));
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(8), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(10), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(12), (ushort)(segments * 2 - searchRange));
        int ends = 14, starts = 16 + segments * 2, deltas = starts + segments * 2;
        for (var i = 0; i < segments; i++)
        {
            var (start, end, glyph) = i < bmp.Count ? bmp[i] : (0xFFFF, 0xFFFF, 0);
            BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(ends + i * 2), (ushort)end);
            BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(starts + i * 2), (ushort)start);
            BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(deltas + i * 2), i < bmp.Count ? (ushort)(glyph - start) : (ushort)1);
        }

        var format12 = new byte[16 + runs.Count * 12];
        BinaryPrimitives.WriteUInt16BigEndian(format12, 12);
        BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(4), (uint)format12.Length);
        BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(12), (uint)runs.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            var group = format12.AsSpan(16 + i * 12);
            BinaryPrimitives.WriteUInt32BigEndian(group, (uint)runs[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(group[4..], (uint)runs[i].End);
            BinaryPrimitives.WriteUInt32BigEndian(group[8..], (uint)runs[i].Glyph);
        }

        // Header plus two encoding records: Windows/Unicode BMP and Windows/Unicode full repertoire.
        const int header = 4 + 2 * 8;
        var table = new byte[header + format4.Length + format12.Length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), header);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(14), 10);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(16), (uint)(header + format4.Length));
        format4.CopyTo(table, header);
        format12.CopyTo(table, header + format4.Length);
        return table;
    }

    private static byte[] Assemble(ReadOnlySpan<byte> sfntVersion, SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(count));
        var searchRange = (1 << entrySelector) * 16;
        var headerSize = 12 + count * 16;
        var total = headerSize + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var result = new byte[total];
        var span = result.AsSpan();
        sfntVersion.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(count * 16 - searchRange));

        var position = headerSize;
        var index = 0;
        foreach (var (tag, data) in tables)
        {
            data.CopyTo(span[position..]);
            var entry = span[(12 + index * 16)..];
            Encoding.ASCII.GetBytes(tag).CopyTo(entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], Checksum(span.Slice(position, (data.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)position);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)data.Length);
            position += (data.Length + 3) & ~3;
            index++;
        }

        return result;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 3 < data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        return sum;
    }
}
