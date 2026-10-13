using System.Text;
using Avalonia.Headless.XUnit;
using PdfModifier.Core;
using PdfModifier.Core.Operations;
using PdfModifier.Core.Pdfium;
using PdfModifier.Localization;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using PdfSharp.Pdf;
using SkiaSharp;
using static PdfModifier.Tests.UiTests;
using PdfPages = PdfModifier.Core.Operations.PdfPages;

namespace PdfModifier.Tests;

/// <summary>
/// The look at whether a document keeps to PDF/A. The documents are made here, each with one thing in it that the
/// standard does not allow, so that it is plain what was found and what was not.
/// </summary>
public class ArchiveTests
{
    /// <summary>
    /// Gives a file the metadata packet that marks it as PDF/A, and a colour profile named as what its colours mean.
    /// Both are added as an update at the end of the file, the way signing programs add to a file without writing
    /// it anew: the library the app saves with makes a metadata packet of its own on every save, so the mark could
    /// not be written through it.
    /// </summary>
    private static byte[] Marked(byte[] pdf, int part = 2, string level = "B", bool asElements = false)
    {
        int catalog, pages, next;
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf, false), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            catalog = doc.Internals.Catalog.Reference!.ObjectNumber;
            pages = doc.Internals.Catalog.Elements.GetReference("/Pages")!.ObjectNumber;
            next = doc.Internals.GetAllObjects().Max(obj => obj.Reference?.ObjectNumber ?? 0) + 1;
        }

        var before = long.Parse(System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(pdf), @"startxref\s+(\d+)\s+%%EOF\s*$").Groups[1].Value);
        var mark = asElements
            ? $"<pdfaid:part>{part}</pdfaid:part><pdfaid:conformance>{level}</pdfaid:conformance>"
            : $"<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"{part}\" pdfaid:conformance=\"{level}\"/>";
        var xmp = $"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">{mark}</x:xmpmeta><?xpacket end=\"w\"?>";

        var added = new StringBuilder("\n");
        var places = new Dictionary<int, long>();

        void Object(int number, string body)
        {
            places[number] = pdf.Length + added.Length;
            added.Append($"{number} 0 obj\n{body}\nendobj\n");
        }

        Object(next, $"<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream");
        Object(next + 1, "<< /N 3 /Length 8 >>\nstream\n12345678\nendstream");
        Object(next + 2, $"<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB) /DestOutputProfile {next + 1} 0 R >>");
        Object(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Metadata {next} 0 R /OutputIntents [{next + 2} 0 R] >>");

        var table = pdf.Length + added.Length;
        added.Append($"xref\n{catalog} 1\n{places[catalog]:D10} 00000 n \n{next} 3\n");
        for (var i = 0; i < 3; i++) added.Append($"{places[next + i]:D10} 00000 n \n");
        added.Append($"trailer\n<< /Size {next + 3} /Root {catalog} 0 R /Prev {before} /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        added.Append($"startxref\n{table}\n%%EOF\n");
        return [.. pdf, .. Encoding.Latin1.GetBytes(added.ToString())];
    }

    private static PdfDictionary Stream(PdfDocument doc, byte[] data)
    {
        var stream = new PdfDictionary(doc);
        doc.Internals.AddObject(stream);
        stream.CreateStream(data);
        return stream;
    }

    /// <summary>A page whose text is set in a font that is only named, not carried along.</summary>
    private static unsafe byte[] WithFontLeftOut(string name = "Helvetica") => TestPdf.Compose((page, _) =>
    {
        var font = Native.FPDFText_LoadStandardFont(page.Doc.Handle, name);
        var text = Native.FPDFPageObj_CreateTextObj(page.Doc.Handle, font, 14);
        Native.FPDFText_SetText(text, "Set in a font that every computer is supposed to have");
        Native.FPDFPageObj_Transform(text, 1, 0, 0, 1, 72, 700);
        Native.FPDFPage_InsertObject(page.Handle, text);
        Native.FPDFFont_Close(font);
    });

    private static IEnumerable<ArchiveRule> Rules(ArchiveReport report) => report.Findings.Select(finding => finding.Rule);

    [Fact]
    public void AnOrdinaryDocumentDoesNotSayItIsPdfAAndIsToldWhatIsMissing()
    {
        var report = PdfArchive.Check(TestPdf.WithText("An ordinary letter", "Its second page"));

        Assert.Null(report.Says);
        // Checked against the part most files are made to, since it says nothing itself.
        Assert.Equal(2, report.AgainstPart);
        Assert.Contains(ArchiveRule.Declaration, Rules(report));
        var colours = Assert.Single(report.Findings, finding => finding.Rule == ArchiveRule.OutputIntent);
        Assert.False(colours.Certain);
        // Its font is part of it, and nothing in it runs, is attached or is locked.
        Assert.DoesNotContain(ArchiveRule.Font, Rules(report));
        Assert.DoesNotContain(ArchiveRule.Script, Rules(report));
        Assert.DoesNotContain(ArchiveRule.Attachment, Rules(report));
        Assert.DoesNotContain(ArchiveRule.Encryption, Rules(report));
        Assert.DoesNotContain(ArchiveRule.Transparency, Rules(report));
    }

    [Theory]
    [InlineData(1, "B", false, "1b")]
    [InlineData(2, "B", false, "2b")]
    [InlineData(3, "A", false, "3a")]
    [InlineData(2, "U", true, "2u")]
    public void AFileThatSaysItIsPdfAIsCheckedAgainstThePartItNames(int part, string level, bool asElements, string says)
    {
        var report = PdfArchive.Check(Marked(TestPdf.WithText("Made to last"), part, level, asElements));

        Assert.Equal(says, report.Says);
        Assert.Equal(part, report.AgainstPart);
        Assert.DoesNotContain(ArchiveRule.Declaration, Rules(report));
        Assert.DoesNotContain(ArchiveRule.OutputIntent, Rules(report));
        Assert.DoesNotContain(ArchiveRule.Font, Rules(report));
        Assert.DoesNotContain(report.Findings, finding => finding.Rule != ArchiveRule.Identifier);

        // Asked outright, it is checked against the part that was asked for.
        Assert.Equal(1, PdfArchive.Check(Marked(TestPdf.WithText("Made to last"), part, level), ArchiveLevel.A1).AgainstPart);
    }

    [Fact]
    public void AFontThatIsOnlyNamedIsFoundWithThePagesItIsOn()
    {
        var pdf = PdfPages.Merge([TestPdf.WithText("Its font comes along"), WithFontLeftOut(), TestPdf.WithText("So does this one"), WithFontLeftOut()]);

        var report = PdfArchive.Check(Marked(pdf));

        var font = Assert.Single(report.Findings, finding => finding.Rule == ArchiveRule.Font);
        Assert.Equal("Helvetica", font.Detail);
        Assert.Equal([1, 3], font.Pages);
        Assert.True(font.Certain);
    }

    [Fact]
    public void TransparencyStandsInTheWayOfTheFirstPartOnly()
    {
        using var glass = new SKBitmap(new SKImageInfo(120, 120, SKColorType.Bgra8888, SKAlphaType.Opaque));
        glass.Erase(new SKColor(40, 90, 160));
        var see = TestPdf.Compose((page, fonts) =>
        {
            PageDraw.DrawText(page, fonts, "Under a picture one can see through", new TextStyle(14), 72, 100);
            PageDraw.DrawImage(page, glass, new RectD(60, 60, 300, 200), opacity: 0.5);
        });
        var pdf = PdfPages.Merge([TestPdf.WithText("Solid"), see]);

        var first = PdfArchive.Check(Marked(pdf, 1));
        var second = PdfArchive.Check(Marked(pdf, 2));

        Assert.Contains(1, Assert.Single(first.Findings, finding => finding.Rule == ArchiveRule.Transparency).Pages);
        Assert.DoesNotContain(ArchiveRule.Transparency, Rules(second));
    }

    [Fact]
    public void WhatRunsByItselfIsAttachedOrLeavesItsLooksToTheReaderIsFound()
    {
        var pdf = Sharp.Rewrite(Marked(TestPdf.WithText("With more in it than meets the eye")), doc =>
        {
            var catalog = doc.Internals.Catalog;
            var script = new PdfDictionary(doc);
            doc.Internals.AddObject(script);
            script.Elements.SetName("/S", "/JavaScript");
            script.Elements.SetString("/JS", "app.alert('hello');");
            catalog.Elements["/OpenAction"] = script.Reference!;

            var launch = new PdfDictionary(doc);
            doc.Internals.AddObject(launch);
            launch.Elements.SetName("/Type", "/Action");
            launch.Elements.SetName("/S", "/Launch");
            catalog.Elements["/TestLaunch"] = launch.Reference!;

            var names = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = new PdfDictionary(doc);
            catalog.Elements["/Names"] = names;

            var form = new PdfDictionary(doc);
            form.Elements["/XFA"] = new PdfArray(doc);
            form.Elements.SetBoolean("/NeedAppearances", true);
            catalog.Elements["/AcroForm"] = form;

            var packed = Stream(doc, new byte[64]);
            packed.Elements.SetName("/Filter", "/LZWDecode");
            catalog.Elements["/TestStream"] = packed.Reference!;
        });

        var second = PdfArchive.Check(pdf);
        var first = PdfArchive.Check(pdf, ArchiveLevel.A1);
        var third = PdfArchive.Check(pdf, ArchiveLevel.A3);

        Assert.Equal(["JavaScript", "Launch"], second.Findings.Where(finding => finding.Rule == ArchiveRule.Script).Select(finding => finding.Detail));
        Assert.Equal(["XFA", "NeedAppearances"], second.Findings.Where(finding => finding.Rule == ArchiveRule.Form).Select(finding => finding.Detail));
        Assert.Equal("LZWDecode", Assert.Single(second.Findings, finding => finding.Rule == ArchiveRule.Compression).Detail);
        // An attachment: barred by the first part, allowed by the third, and in between only if it is PDF/A itself.
        Assert.True(Assert.Single(first.Findings, finding => finding.Rule == ArchiveRule.Attachment).Certain);
        Assert.False(Assert.Single(second.Findings, finding => finding.Rule == ArchiveRule.Attachment).Certain);
        Assert.DoesNotContain(ArchiveRule.Attachment, Rules(third));
    }

    [Fact]
    public void AnEncryptedFileIsNotPdfAWhateverIsInIt()
    {
        var report = PdfArchive.Check(PdfSecurity.Protect(Marked(TestPdf.WithText("Locked away")), "secret"));

        Assert.Equal([ArchiveRule.Encryption], Rules(report));
        Assert.Null(report.Says);
    }

    [AvaloniaFact]
    public async Task TheToolSaysWhatStandsInTheWayAndLeadsToThePagesItIsOn()
    {
        var (window, vm) = Start();
        vm.Session.Open(PdfPages.Merge([TestPdf.WithText("Its font comes along"), WithFontLeftOut(), WithFontLeftOut()]), null);
        var tool = Tool<ArchiveTool>(vm);
        var verdict = tool.Fields.OfType<InfoField>().First();
        var findings = Field<ListField>(tool, "ArchiveFindings");
        await Settle(() => findings.HasItems);

        // Looked at when the tool is opened: it says nothing about being PDF/A, and three things stand in the way.
        Assert.StartsWith(Strings.English["ArchiveSaysNothing"], verdict.Text);
        Assert.True(verdict.Warns);
        var font = Assert.Single(findings.Items, item => item.Title == string.Format(Strings.English["ArchiveRuleFont"], "Helvetica"));
        Assert.Equal(string.Format(Strings.English["ArchivePages"], "2-3"), font.Detail);
        Assert.False(font.Warns);
        // What cannot be told for certain says so and is marked red.
        var colours = Assert.Single(findings.Items, item => item.Title.StartsWith(Strings.English["ArchiveRuleOutputIntent"]));
        Assert.True(colours.Warns);
        Assert.Contains(Strings.English["ArchiveRuleDeclaration"], findings.Items.Select(item => item.Title));
        Assert.Equal(Strings.English["ArchiveWholeFile"], findings.Items.First(item => item.Title == Strings.English["ArchiveRuleDeclaration"]).Detail);
        // The pages concerned are marked in the page list, and a click on the finding goes to the first of them.
        Assert.Null(vm.Pages[0].Badge);
        Assert.True(vm.Pages[1].Badge!.Warns);
        Assert.True(vm.Pages[2].Badge!.Warns);
        findings.Selected = font;
        await Settle(() => vm.CurrentPageIndex == 1);
        Assert.Equal(1, vm.CurrentPageIndex);
        Assert.True(vm.Pages[2].Badge!.Warns);
        Capture(window, "68-archive");

        // The document changes: the look is taken again by itself.
        vm.Commit(PdfPages.Delete(vm.Session.Bytes!, [1, 2]));
        await Settle(() => findings.HasItems && findings.Items.All(item => !item.Title.Contains("Helvetica")));
        Assert.DoesNotContain(findings.Items, item => item.Title.Contains("Helvetica"));
        Assert.All(vm.Pages, page => Assert.Null(page.Badge));

        // A file that is marked and has what the mark promises: nothing stands in the way.
        vm.Session.Open(Marked(TestPdf.WithText("Made to last")), null);
        await Settle(() => verdict.Text.StartsWith(string.Format(Strings.English["ArchiveSays"], "2b")));
        Assert.StartsWith(string.Format(Strings.English["ArchiveSays"], "2b"), verdict.Text);
        window.Close();
    }
}
