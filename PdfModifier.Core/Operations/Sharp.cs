using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfModifier.Core.Operations;

/// <summary>PDFsharp-based passes for the things PDFium has no API for: info dictionary edits, encryption and garbage collection.</summary>
internal static partial class Sharp
{
    /// <summary>Opens, optionally mutates and re-saves a document. PDFsharp drops unreachable objects on save.</summary>
    public static byte[] Rewrite(byte[] pdf, Action<PdfDocument>? mutate = null)
    {
        using var input = new MemoryStream(pdf, false);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        mutate?.Invoke(doc);
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    /// <summary>
    /// Removes objects that are no longer referenced (deleted pages, replaced images).
    /// Falls back to the input when PDFsharp cannot parse the file, so callers never lose a working document.
    /// </summary>
    public static byte[] Compact(byte[] pdf)
    {
        try
        {
            var result = Rewrite(pdf);
            return result.Length < pdf.Length ? result : pdf;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return pdf;
        }
    }

    /// <summary>Runs <see cref="Deduplicate"/> on a document; returns the input when that fails or does not help.</summary>
    public static byte[] ShareDuplicates(byte[] pdf)
    {
        try
        {
            var result = Rewrite(pdf, Deduplicate);
            return result.Length < pdf.Length ? result : pdf;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return pdf;
        }
    }

    /// <summary>
    /// Makes identical images, embedded font programs and colour profiles share one copy. Merging documents and stamping
    /// pages in batches both leave such duplicates behind. Page content streams are deliberately left alone: two pages
    /// sharing one would change together when either is edited later.
    /// </summary>
    public static void Deduplicate(PdfDocument doc)
    {
        // Leaves first: two images only become identical once their (identical) masks have been merged.
        for (var pass = 0; pass < 3; pass++)
        {
            var objects = doc.Internals.GetAllObjects();
            var canonical = new Dictionary<string, PdfReference>();
            var replacements = new Dictionary<PdfReference, PdfReference>(ReferenceEqualityComparer.Instance);
            foreach (var obj in objects)
            {
                if (obj is not PdfDictionary { Stream.Value: { Length: >= 256 } data } dict || dict.Reference == null) continue;
                if (!IsShareable(dict)) continue;
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) + Describe(dict, true);
                if (canonical.TryGetValue(key, out var first)) replacements[dict.Reference] = first;
                else canonical[key] = dict.Reference;
            }

            if (replacements.Count == 0) return;
            foreach (var obj in objects) Redirect(obj, replacements);
        }
    }

    private static bool IsShareable(PdfDictionary dict)
    {
        var e = dict.Elements;
        if (e.GetName("/Subtype") == "/Image") return true;
        // Font programs: FontFile/FontFile2 carry /Length1, FontFile3 a font subtype.
        if (e.ContainsKey("/Length1") || e.GetName("/Subtype") is "/Type1C" or "/CIDFontType0C" or "/OpenType") return true;
        // ICC profiles.
        return e.ContainsKey("/N") && !e.ContainsKey("/Type");
    }

    /// <summary>Canonical text of an object's entries, so equal dictionaries compare equal regardless of key order.</summary>
    private static string Describe(PdfItem? item, bool top = false) => item switch
    {
        null => "null",
        PdfReference reference => $"R{reference.ObjectNumber}",
        PdfDictionary dict => "{" + string.Join(";", dict.Elements.KeyNames
            .Select(k => k.Value)
            .Where(k => !(top && k == "/Length"))
            .Order(StringComparer.Ordinal)
            .Select(k => k + "=" + Describe(dict.Elements[k]))) + "}",
        PdfArray array => "[" + string.Join(",", array.Elements.Select(i => Describe(i))) + "]",
        _ => item.GetType().Name + ":" + item
    };

    private static void Redirect(PdfItem? item, Dictionary<PdfReference, PdfReference> replacements)
    {
        switch (item)
        {
            case PdfDictionary dict:
                foreach (var key in dict.Elements.KeyNames)
                {
                    var value = dict.Elements[key.Value];
                    if (value is PdfReference reference)
                    {
                        if (replacements.TryGetValue(reference, out var target)) dict.Elements[key.Value] = target;
                    }
                    else
                    {
                        Redirect(value, replacements);
                    }
                }

                break;
            case PdfArray array:
                for (var i = 0; i < array.Elements.Count; i++)
                {
                    var value = array.Elements[i];
                    if (value is PdfReference reference)
                    {
                        if (replacements.TryGetValue(reference, out var target)) array.Elements[i] = target;
                    }
                    else
                    {
                        Redirect(value, replacements);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Blanks the /Info reference in the trailer so the info dictionary PDFsharp always stamps (producer, dates) is detached.
    /// The replacement has the same length, so no byte offsets move.
    /// </summary>
    public static byte[] DetachInfoDictionary(byte[] pdf)
    {
        var tailStart = Math.Max(0, pdf.Length - 4096);
        var tail = Encoding.Latin1.GetString(pdf, tailStart, pdf.Length - tailStart);
        var trailer = tail.LastIndexOf("trailer", StringComparison.Ordinal);
        if (trailer < 0) return pdf;
        var match = InfoReference().Match(tail, trailer);
        if (!match.Success) return pdf;
        var result = (byte[])pdf.Clone();
        result.AsSpan(tailStart + match.Index, match.Length).Fill((byte)' ');
        return result;
    }

    [GeneratedRegex(@"/Info\s+\d+\s+\d+\s+R")]
    private static partial Regex InfoReference();
}
