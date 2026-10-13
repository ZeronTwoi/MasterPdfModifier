using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfModifier.Core.Operations;

/// <summary>Which part of PDF/A a document is checked against.</summary>
public enum ArchiveLevel
{
    /// <summary>The one the file says it is; PDF/A-2 for a file that says nothing.</summary>
    Declared,
    A1,
    A2,
    A3
}

/// <summary>The kinds of thing that stand in the way of PDF/A, as far as this check looks for them.</summary>
public enum ArchiveRule
{
    /// <summary>The file does not say in its metadata that it is PDF/A.</summary>
    Declaration,
    Encryption,

    /// <summary>A font the pages use is not part of the file, so another computer may show other letters.</summary>
    Font,

    /// <summary>No colour profile says what the colours of the document mean.</summary>
    OutputIntent,

    /// <summary>Transparency, which the first part of the standard does not know.</summary>
    Transparency,

    /// <summary>JavaScript, or an action that starts a program, plays something or changes a form.</summary>
    Script,
    Attachment,

    /// <summary>A way of packing data that the standard does not allow.</summary>
    Compression,

    /// <summary>A form whose looks are left to the reader (XFA, or fields without a picture of themselves).</summary>
    Form,
    Annotation,

    /// <summary>A way of laying out the file that is newer than the first part of the standard.</summary>
    Structure,

    /// <summary>The file has no identifier of its own.</summary>
    Identifier
}

/// <summary>
/// One thing that stands in the way: what kind, what exactly (the name of a font, of a filter), the pages it is
/// on (none when it is about the file as a whole), and whether it is sure to break the rule or only likely to.
/// </summary>
public sealed record ArchiveFinding(ArchiveRule Rule, string Detail, IReadOnlyList<int> Pages, bool Certain = true);

/// <summary>
/// What a look at a document gave: the part and level of PDF/A it says it is (none when it says nothing), the part
/// it was checked against, and what stands in the way.
/// </summary>
public sealed record ArchiveReport(int? Part, string? Conformance, ArchiveLevel Against, IReadOnlyList<ArchiveFinding> Findings)
{
    /// <summary>What the file calls itself, such as "2b"; null when it says nothing.</summary>
    public string? Says => Part is { } part ? part + (Conformance ?? "").ToLowerInvariant() : null;

    /// <summary>The number of the part it was checked against.</summary>
    public int AgainstPart => Against switch { ArchiveLevel.A1 => 1, ArchiveLevel.A3 => 3, _ => 2 };
}

/// <summary>
/// Looks whether a document keeps to PDF/A, the standard for files that have to stay readable for decades: all it
/// needs to be shown is inside it, and nothing in it depends on a program, a password or a computer. This looks at
/// what most often stands in the way. It is not a full validation: the standard has hundreds of rules, and an
/// archive's own validator has the last word.
/// </summary>
public static partial class PdfArchive
{
    private static readonly HashSet<string> RunsSomething = ["/JavaScript", "/Launch", "/Sound", "/Movie", "/ResetForm", "/ImportData", "/Rendition", "/SetOCGState", "/Trans", "/GoTo3DView"];
    private static readonly HashSet<string> BarredAlways = ["/3D", "/Sound", "/Screen", "/Movie"];

    public static ArchiveReport Check(byte[] pdf, ArchiveLevel against = ArchiveLevel.Declared, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        var findings = new List<ArchiveFinding>();
        var grouped = new SortedSet<int>();
        var encrypted = Contains(pdf, "/Encrypt");
        if (encrypted) findings.Add(new ArchiveFinding(ArchiveRule.Encryption, "", []));

        int? part = null;
        string? conformance = null;
        PdfDocument? doc = null;
        try
        {
            doc = PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Import);
        }
        catch (Exception e) when (e is not OutOfMemoryException && encrypted)
        {
            // An encrypted file cannot be looked into without its password, and is not PDF/A whatever is in it.
            return new ArchiveReport(null, null, against == ArchiveLevel.Declared ? ArchiveLevel.A2 : against, findings);
        }

        using (doc)
        {
            var catalog = doc.Internals.Catalog;
            if (Metadata(catalog) is { } xmp)
            {
                var said = PartMark().Match(xmp);
                if (said.Success) part = int.Parse(said.Groups[1].Success ? said.Groups[1].Value : said.Groups[2].Value);
                var level = LevelMark().Match(xmp);
                if (level.Success) conformance = level.Groups[1].Success ? level.Groups[1].Value : level.Groups[2].Value;
            }

            if (against == ArchiveLevel.Declared) against = part switch { 1 => ArchiveLevel.A1, 3 => ArchiveLevel.A3, _ => ArchiveLevel.A2 };
            var first = against == ArchiveLevel.A1;
            if (part == null) findings.Add(new ArchiveFinding(ArchiveRule.Declaration, "", []));
            if (string.IsNullOrEmpty(doc.Internals.FirstDocumentID)) findings.Add(new ArchiveFinding(ArchiveRule.Identifier, "", []));
            if (!HasOutputIntent(catalog)) findings.Add(new ArchiveFinding(ArchiveRule.OutputIntent, "", [], Certain: false));

            // Page by page: the fonts the page has to hand, and the annotations on it.
            var fonts = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
            var annotations = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
            for (var page = 0; page < doc.PageCount; page++)
            {
                cancel.ThrowIfCancellationRequested();
                var elements = doc.Pages[page].Elements;
                // A page that is declared a transparency group is barred by the first part, whatever is on it.
                if (first && elements.GetDictionary("/Group") is { } group && group.Elements.GetName("/S") == "/Transparency") grouped.Add(page);
                if (ResourcesOf(elements.GetDictionary("/Resources"), page, fonts, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0) && first)
                    grouped.Add(page);
                if (elements.GetArray("/Annots") is { } list)
                    foreach (var item in list.Elements)
                    {
                        if (Resolve(item) is not PdfDictionary annotation) continue;
                        var kind = annotation.Elements.GetName("/Subtype");
                        if (BarredAlways.Contains(kind) || (first && kind == "/FileAttachment")) Note(annotations, kind.TrimStart('/'), page);
                    }

                progress?.Report((page + 1.0) / doc.PageCount * 0.9);
            }

            findings.AddRange(fonts.Select(font => new ArchiveFinding(ArchiveRule.Font, font.Key, font.Value.ToList())));
            findings.AddRange(annotations.Select(kind => new ArchiveFinding(ArchiveRule.Annotation, kind.Key, kind.Value.ToList())));

            // And everything the file holds, wherever it hangs: what runs something, what is attached, how data is packed.
            var scripts = new SortedSet<string>(StringComparer.Ordinal);
            var filters = new SortedSet<string>(StringComparer.Ordinal);
            var attached = false;
            foreach (var obj in doc.Internals.GetAllObjects())
            {
                if (obj is not PdfDictionary dict) continue;
                var e = dict.Elements;
                if (e.ContainsKey("/JS")) scripts.Add("JavaScript");
                else if (e.GetName("/S") is { Length: > 0 } action && RunsSomething.Contains(action) && (e.GetName("/Type") is "/Action" or "" )) scripts.Add(action.TrimStart('/'));
                if (e.GetName("/Type") == "/Filespec" && e.ContainsKey("/EF")) attached = true;
                if (dict.Stream != null)
                    foreach (var filter in FiltersOf(e))
                        if (filter == "/LZWDecode" || (first && filter == "/JPXDecode"))
                            filters.Add(filter.TrimStart('/'));
            }

            if (catalog.Elements.GetDictionary("/Names") is { } names)
            {
                if (names.Elements.ContainsKey("/JavaScript")) scripts.Add("JavaScript");
                if (names.Elements.ContainsKey("/EmbeddedFiles")) attached = true;
            }

            findings.AddRange(scripts.Select(script => new ArchiveFinding(ArchiveRule.Script, script, [])));
            findings.AddRange(filters.Select(filter => new ArchiveFinding(ArchiveRule.Compression, filter, [])));
            // The third part allows attachments of any kind; the second only such as are PDF/A themselves, which is not looked into here.
            if (attached && against != ArchiveLevel.A3) findings.Add(new ArchiveFinding(ArchiveRule.Attachment, "", [], Certain: first));

            if (catalog.Elements.GetDictionary("/AcroForm") is { } form)
            {
                if (form.Elements.ContainsKey("/XFA")) findings.Add(new ArchiveFinding(ArchiveRule.Form, "XFA", []));
                if (form.Elements.GetBoolean("/NeedAppearances")) findings.Add(new ArchiveFinding(ArchiveRule.Form, "NeedAppearances", []));
            }

            if (first && Contains(pdf, "/ObjStm")) findings.Add(new ArchiveFinding(ArchiveRule.Structure, "", []));
        }

        if (grouped.Count > 0) findings.Add(new ArchiveFinding(ArchiveRule.Transparency, "", grouped.ToList()));
        progress?.Report(1);
        return new ArchiveReport(part, conformance, against, findings);
    }

    private static void Note(SortedDictionary<string, SortedSet<int>> found, string what, int page) =>
        (found.TryGetValue(what, out var pages) ? pages : found[what] = []).Add(page);

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    /// <summary>The metadata packet of the document as text; null when it has none.</summary>
    private static string? Metadata(PdfDictionary catalog)
    {
        if (catalog.Elements.GetDictionary("/Metadata") is not { Stream: { } stream }) return null;
        byte[] bytes;
        try
        {
            bytes = stream.UnfilteredValue;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            bytes = stream.Value;
        }

        return bytes.Length >= 2 && (bytes[0], bytes[1]) is (0xFE, 0xFF) ? Encoding.BigEndianUnicode.GetString(bytes)
            : bytes.Length >= 2 && (bytes[0], bytes[1]) is (0xFF, 0xFE) ? Encoding.Unicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>True when a colour profile is named as what the colours of the document are meant for.</summary>
    private static bool HasOutputIntent(PdfDictionary catalog)
    {
        if (catalog.Elements.GetArray("/OutputIntents") is not { } intents) return false;
        return intents.Elements.Select(Resolve).OfType<PdfDictionary>()
            .Any(intent => intent.Elements.GetName("/S") is "/GTS_PDFA1" or "/GTS_PDFX" && intent.Elements.ContainsKey("/DestOutputProfile"));
    }

    /// <summary>
    /// Goes through what a page has to hand, its own and that of the graphics nested in it. Notes the fonts that
    /// are not part of the file, and returns whether anything there is see-through: a picture with a mask of its
    /// own, a way of drawing that lets what is under it show, or a nested graphic that is a transparency group.
    /// </summary>
    private static bool ResourcesOf(PdfDictionary? resources, int page, SortedDictionary<string, SortedSet<int>> missing, HashSet<PdfDictionary> seen, int depth)
    {
        if (resources == null || depth > 8 || !seen.Add(resources)) return false;
        var seeThrough = false;
        if (resources.Elements.GetDictionary("/Font") is { } fonts)
            foreach (var key in fonts.Elements.Keys)
                if (Resolve(fonts.Elements[key]) is PdfDictionary font && !IsEmbedded(font))
                    Note(missing, FontName(font), page);
        if (resources.Elements.GetDictionary("/ExtGState") is { } states)
            foreach (var key in states.Elements.Keys)
                if (Resolve(states.Elements[key]) is PdfDictionary state)
                {
                    var e = state.Elements;
                    seeThrough |= (e.ContainsKey("/CA") && e.GetReal("/CA") < 0.999) || (e.ContainsKey("/ca") && e.GetReal("/ca") < 0.999) ||
                                  (e.ContainsKey("/SMask") && Resolve(e["/SMask"]) is not PdfName { Value: "/None" }) ||
                                  (Resolve(e["/BM"]) is PdfName blend && blend.Value is not ("/Normal" or "/Compatible"));
                }

        if (resources.Elements.GetDictionary("/XObject") is { } graphics)
            foreach (var key in graphics.Elements.Keys)
            {
                if (Resolve(graphics.Elements[key]) is not PdfDictionary graphic) continue;
                if (graphic.Elements.ContainsKey("/SMask") && Resolve(graphic.Elements["/SMask"]) is not PdfName { Value: "/None" }) seeThrough = true;
                if (graphic.Elements.GetName("/Subtype") != "/Form") continue;
                if (graphic.Elements.GetDictionary("/Group") is { } group && group.Elements.GetName("/S") == "/Transparency") seeThrough = true;
                seeThrough |= ResourcesOf(graphic.Elements.GetDictionary("/Resources"), page, missing, seen, depth + 1);
            }

        return seeThrough;
    }

    private static bool IsEmbedded(PdfDictionary font)
    {
        switch (font.Elements.GetName("/Subtype"))
        {
            // Its letters are drawn by the file itself.
            case "/Type3":
                return true;
            case "/Type0":
                return font.Elements.GetArray("/DescendantFonts") is { Elements.Count: > 0 } descendants &&
                       Resolve(descendants.Elements[0]) is PdfDictionary descendant && HasProgram(descendant);
            default:
                return HasProgram(font);
        }
    }

    private static bool HasProgram(PdfDictionary font) => font.Elements.GetDictionary("/FontDescriptor") is { } descriptor &&
                                                           (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") ||
                                                            descriptor.Elements.ContainsKey("/FontFile3"));

    /// <summary>The name of a font as people know it, without the six letters a part of a font is marked with.</summary>
    private static string FontName(PdfDictionary font)
    {
        var name = font.Elements.GetName("/BaseFont").TrimStart('/');
        if (name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper)) name = name[7..];
        return name.Length == 0 ? "?" : name.Replace("#20", " ");
    }

    private static IEnumerable<string> FiltersOf(PdfDictionary.DictionaryElements elements)
    {
        var filter = Resolve(elements["/Filter"]);
        if (filter is PdfName one) return [one.Value];
        if (filter is PdfArray several) return several.Elements.Select(Resolve).OfType<PdfName>().Select(name => name.Value);
        return [];
    }

    /// <summary>True when the bytes of the file hold this word, which is quicker than reading the file to find out.</summary>
    private static bool Contains(byte[] pdf, string word) => pdf.AsSpan().IndexOf(Encoding.ASCII.GetBytes(word)) >= 0;

    [GeneratedRegex(@"pdfaid:part\s*=\s*[""'](\d)[""']|<pdfaid:part>\s*(\d)\s*</pdfaid:part>")]
    private static partial Regex PartMark();

    [GeneratedRegex(@"pdfaid:conformance\s*=\s*[""']([A-Za-z])[""']|<pdfaid:conformance>\s*([A-Za-z])\s*</pdfaid:conformance>")]
    private static partial Regex LevelMark();
}
