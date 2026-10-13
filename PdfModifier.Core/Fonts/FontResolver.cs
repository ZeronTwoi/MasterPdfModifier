using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace PdfModifier.Core.Fonts;

/// <summary>An installed TrueType font together with the bytes PDFium needs to embed it.</summary>
internal sealed class ResolvedFont(SKTypeface typeface, byte[] data)
{
    private const float Em = 1000;

    // Measured at a large size without hinting and scaled down, the way a PDF viewer places glyphs. Hinted advances
    // of small text are rounded to whole pixels and would not match what ends up in the document.
    private readonly SKPaint _linear = Linear(typeface, Em);

    public SKTypeface Typeface { get; } = typeface;
    public byte[] Data { get; } = data;
    public string Family => Typeface.FamilyName;

    public bool Covers(int codePoint) => Typeface.GetGlyph(codePoint) != 0;

    public double Measure(string text, double size)
    {
        lock (_linear) return _linear.MeasureText(text) * size / Em;
    }

    /// <summary>Distance from the baseline to the top of tall letters and to the bottom of hanging ones.</summary>
    public (double Ascent, double Descent) Metrics(double size)
    {
        lock (_linear)
        {
            var metrics = _linear.FontMetrics;
            return (-metrics.Ascent * size / Em, metrics.Descent * size / Em);
        }
    }

    /// <summary>Draws text the way the document will show it: glyph after glyph at unhinted advances.</summary>
    public void Draw(SKCanvas canvas, string text, double x, double baseline, double size, SKColor color)
    {
        using var paint = Linear(Typeface, (float)size);
        paint.Color = color;
        paint.IsAntialias = true;
        canvas.DrawText(text, (float)x, (float)baseline, paint);
    }

    private static SKPaint Linear(SKTypeface typeface, float size) => new()
    {
        Typeface = typeface, TextSize = size, IsLinearText = true, SubpixelText = true, HintingLevel = SKPaintHinting.NoHinting
    };
}

/// <summary>
/// Finds installed fonts for new text. PDFium can only embed TrueType outlines, so CFF-based OpenType fonts are skipped.
/// </summary>
internal static class FontResolver
{
    private static readonly string[] Sans = ["Arial", "Segoe UI", "Liberation Sans", "DejaVu Sans", "Noto Sans", "Helvetica", "FreeSans"];
    private static readonly string[] Serif = ["Times New Roman", "Liberation Serif", "DejaVu Serif", "Noto Serif", "FreeSerif"];
    private static readonly string[] Mono = ["Courier New", "Consolas", "Liberation Mono", "DejaVu Sans Mono", "Noto Sans Mono", "FreeMono"];

    private static readonly ConcurrentDictionary<string, ResolvedFont?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<IReadOnlyList<string>> Families = new(() => SKFontManager.Default.FontFamilies
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(IsEmbeddable)
        .Order(StringComparer.CurrentCultureIgnoreCase)
        .ToList());

    /// <summary>The installed families new text can be set in, sorted by name. Listed once; the list is cached.</summary>
    public static IReadOnlyList<string> EmbeddableFamilies() => Families.Value;

    private static bool IsEmbeddable(string family)
    {
        // Looks at the table directory only, so hundreds of fonts are checked without reading their outlines.
        const uint glyf = 0x676C7966;
        using var typeface = SKTypeface.FromFamilyName(family);
        return typeface != null && typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase) &&
               typeface.GetTableSize(glyf) > 0;
    }

    /// <summary>Resolves a family name (or "serif"/"monospace"/null for the generic defaults) to an embeddable font.</summary>
    public static ResolvedFont? Resolve(string? family, bool bold = false, bool italic = false)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(family) && !IsGeneric(family)) candidates.Add(family);
        candidates.AddRange(GenericList(family));
        foreach (var name in candidates)
        {
            var font = Cache.GetOrAdd($"{name}|{bold}|{italic}", _ => LoadFamily(name, bold, italic));
            if (font != null) return font;
        }

        return null;
    }

    /// <summary>
    /// Splits text into runs that a single font can draw, preferring <paramref name="family"/> and falling back to
    /// whatever installed font covers the characters it lacks (Cyrillic, Japanese, ...).
    /// </summary>
    public static List<(string Text, ResolvedFont Font)> Itemize(string text, string? family, bool bold = false, bool italic = false)
    {
        // A no-break space shares its glyph with the ordinary space; keeping both would make extracted text ambiguous.
        text = text.Replace(' ', ' ');
        var runs = new List<(string, ResolvedFont)>();
        var primary = Resolve(family, bold, italic)
                      ?? throw new InvalidOperationException("No usable TrueType font is installed on this system.");
        var current = new StringBuilder();
        var currentFont = primary;

        foreach (var rune in text.EnumerateRunes())
        {
            ResolvedFont font;
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) font = currentFont; // whitespace never forces a font switch
            else if (currentFont.Covers(rune.Value)) font = currentFont;
            else if (primary.Covers(rune.Value)) font = primary;
            else font = ResolveForCharacter(rune.Value, bold, italic) ?? primary;

            if (!ReferenceEquals(font, currentFont) && current.Length > 0)
            {
                runs.Add((current.ToString(), currentFont));
                current.Clear();
            }

            currentFont = font;
            current.Append(rune.ToString());
        }

        if (current.Length > 0) runs.Add((current.ToString(), currentFont));
        return runs;
    }

    public static double Measure(string text, string? family, bool bold, bool italic, double size) =>
        Itemize(text, family, bold, italic).Sum(run => run.Font.Measure(run.Text, size));

    private static ResolvedFont? ResolveForCharacter(int codePoint, bool bold, bool italic)
    {
        var style = Style(bold, italic);
        var typeface = SKFontManager.Default.MatchCharacter(null, style, null, codePoint);
        if (typeface == null) return null;
        return Cache.GetOrAdd($"{typeface.FamilyName}|{bold}|{italic}", _ => Load(typeface));
    }

    private static ResolvedFont? LoadFamily(string name, bool bold, bool italic)
    {
        var typeface = SKTypeface.FromFamilyName(name, Style(bold, italic));
        // Skia silently substitutes its default font for unknown families.
        if (typeface == null || !typeface.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)) return null;
        return Load(typeface);
    }

    private static ResolvedFont? Load(SKTypeface typeface)
    {
        using var stream = typeface.OpenStream(out var ttcIndex);
        if (stream == null || stream.Length <= 0) return null;
        var data = new byte[stream.Length];
        if (stream.Read(data, data.Length) != data.Length) return null;
        if (data.AsSpan(0, 4).SequenceEqual("ttcf"u8)) data = ExtractFromCollection(data, ttcIndex);
        return data != null && HasTable(data, "glyf") ? new ResolvedFont(typeface, data) : null;
    }

    private static SKFontStyle Style(bool bold, bool italic) => new(
        bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
        SKFontStyleWidth.Normal,
        italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private static bool IsGeneric(string family) =>
        family.Equals("serif", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("monospace", StringComparison.OrdinalIgnoreCase);

    private static string[] GenericList(string? family)
    {
        if (family != null && family.Equals("serif", StringComparison.OrdinalIgnoreCase)) return Serif;
        if (family != null && family.Equals("monospace", StringComparison.OrdinalIgnoreCase)) return Mono;
        return Sans;
    }

    /// <summary>
    /// Turns a PDF base font name such as "ABCDEF+TimesNewRomanPS-BoldItalicMT" into a family to look up plus style flags.
    /// <paramref name="fontFlags"/> are the PDF font descriptor flags (1 = fixed pitch, 2 = serif, 64 = italic).
    /// </summary>
    public static (string Family, bool Bold, bool Italic) ParsePdfFontName(string baseFont, int fontFlags = 0, int weight = 0)
    {
        var name = baseFont;
        var plus = name.IndexOf('+');
        if (plus is > 0 and <= 7) name = name[(plus + 1)..];

        var bold = name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || weight >= 600;
        var italic = name.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Oblique", StringComparison.OrdinalIgnoreCase) || (fontFlags & 64) != 0;

        var cut = name.IndexOfAny(['-', ',']);
        if (cut > 0) name = name[..cut];
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length + 2)
            {
                name = name[..^suffix.Length];
                break;
            }

        foreach (var word in new[] { "BoldItalic", "BoldOblique", "Bold", "Italic", "Oblique", "Regular" })
            if (name.EndsWith(word, StringComparison.OrdinalIgnoreCase) && name.Length > word.Length)
                name = name[..^word.Length];

        // "TimesNewRoman" -> "Times New Roman"
        var spaced = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) spaced.Append(' ');
            spaced.Append(name[i]);
        }

        var family = spaced.ToString().Trim();
        family = family switch
        {
            "Helvetica" or "Helvetica Neue" => "Arial",
            "Times" or "Times Roman" => "Times New Roman",
            "Courier" => "Courier New",
            _ => family
        };

        // Not installed: keep the style class (serif / monospace) so the replacement looks related.
        if (SKTypeface.FromFamilyName(family) is not { } probe || !probe.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
            family = (fontFlags & 1) != 0 ? "monospace" : (fontFlags & 2) != 0 ? "serif" : "sans-serif";

        return (family, bold, italic);
    }

    private static bool HasTable(byte[] sfnt, string tag)
    {
        if (sfnt.Length < 12) return false;
        var count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var entry = 12 + i * 16;
            if (entry + 16 > sfnt.Length) return false;
            if (Encoding.ASCII.GetString(sfnt, entry, 4) == tag) return true;
        }

        return false;
    }

    /// <summary>Copies one face out of a TrueType collection into a standalone font file.</summary>
    internal static byte[]? ExtractFromCollection(byte[] ttc, int index)
    {
        var span = ttc.AsSpan();
        var fontCount = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        if (index < 0 || index >= fontCount) return null;
        var directory = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(12 + index * 4)..]);
        if (directory + 12 > ttc.Length) return null;
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(span[(directory + 4)..]);

        var headerSize = 12 + tableCount * 16;
        using var output = new MemoryStream();
        output.Write(span.Slice(directory, headerSize));

        var offsets = new uint[tableCount];
        for (var i = 0; i < tableCount; i++)
        {
            var entry = directory + 12 + i * 16;
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 8)..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 12)..]);
            if (offset < 0 || length < 0 || offset + length > ttc.Length) return null;
            offsets[i] = (uint)output.Position;
            output.Write(span.Slice(offset, length));
            while (output.Position % 4 != 0) output.WriteByte(0);
        }

        var result = output.ToArray();
        for (var i = 0; i < tableCount; i++)
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + i * 16 + 8), offsets[i]);
        return result;
    }
}
