using PdfModifier.Core.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfPage = PdfModifier.Core.Pdfium.PdfPage;

namespace PdfModifier.Core.Operations;

/// <summary>
/// Applies content changes to many pages without the cost growing with the square of the document size.
/// <para>
/// PDFium regenerates a page's content with a pass over every page (and, in typical files, every object) of the
/// document it belongs to. That is fine for a few pages but adds up to minutes when hundreds of pages of a large
/// document are stamped or cleaned. For such jobs the pages are copied in small batches into scratch documents, edited
/// there, and their new content and resources are then put back onto the original page objects. The pages themselves
/// are never replaced, so bookmarks, links, form fields and anything else pointing at them keep working.
/// </para>
/// </summary>
internal static class ContentEditor
{
    /// <summary>Changes one page. Returns true when the page content has to be rewritten.</summary>
    public delegate bool Edit(PdfPage page, int originalIndex, DocFonts fonts);

    private const int ChunkSize = 64;

    /// <summary>Above this product of edited pages × document pages the in-place overhead becomes noticeable.</summary>
    private const long InPlaceBudget = 20_000;

    /// <summary>Test hook: forces the scratch-document path with this batch size on the current thread.</summary>
    [ThreadStatic] internal static int? ForcedChunkSize;

    private static readonly string[] ResourceCategories =
        ["/Font", "/XObject", "/ExtGState", "/ColorSpace", "/Pattern", "/Shading", "/Properties"];

    /// <summary>
    /// Runs <paramref name="edit"/> on the selected pages. <paramref name="prepare"/> announces the text a batch of pages
    /// is going to draw, so fonts are embedded once per batch. Returns the input instance when no page changed.
    /// </summary>
    public static byte[] Run(byte[] pdf, IReadOnlyCollection<int>? pages, Edit edit,
        Action<DocFonts, IReadOnlyList<int>>? prepare = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        List<int> selected;
        int pageCount;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            pageCount = doc.PageCount;
            selected = PdfPages.ResolvePages(pages, pageCount);
        }

        if (selected.Count == 0) return pdf;
        if (ForcedChunkSize == null && (long)selected.Count * pageCount <= InPlaceBudget)
            return RunInPlace(pdf, selected, edit, prepare, progress, cancel);

        var scratch = RunInScratchDocuments(pdf, selected, edit, prepare, progress, cancel);
        if (scratch.Count == 0) return pdf;
        try
        {
            var result = Transplant(pdf, scratch);
            progress?.Report(1);
            return result;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            // The second PDF library is stricter about damaged files than PDFium. Slow but sure beats failing.
            return RunInPlace(pdf, selected, edit, prepare, progress, cancel);
        }
    }

    private static byte[] RunInPlace(byte[] pdf, List<int> selected, Edit edit,
        Action<DocFonts, IReadOnlyList<int>>? prepare, IProgress<double>? progress, CancellationToken cancel)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var fonts = new DocFonts(doc);
            prepare?.Invoke(fonts, selected);
            var changed = false;
            for (var i = 0; i < selected.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                using var page = doc.LoadPage(selected[i]);
                if (edit(page, selected[i], fonts))
                {
                    page.GenerateContent();
                    changed = true;
                }

                progress?.Report((i + 1.0) / selected.Count);
            }

            return changed ? doc.Save() : pdf;
        }
    }

    /// <summary>One scratch document and, for each changed page in it, the index of the page it came from.</summary>
    private sealed record Batch(byte[] Pdf, List<(int Original, int Scratch)> Pages);

    private static List<Batch> RunInScratchDocuments(byte[] pdf, List<int> selected, Edit edit,
        Action<DocFonts, IReadOnlyList<int>>? prepare, IProgress<double>? progress, CancellationToken cancel)
    {
        var batches = new List<Batch>();
        var size = ForcedChunkSize ?? ChunkSize;
        PdfDoc source;
        lock (PdfDoc.Sync) source = PdfDoc.Load(pdf);
        try
        {
            for (var start = 0; start < selected.Count; start += size)
            {
                cancel.ThrowIfCancellationRequested();
                var chunk = selected.GetRange(start, Math.Min(size, selected.Count - start));
                // The lock is taken per batch so the page view can keep rendering during long jobs.
                lock (PdfDoc.Sync)
                {
                    using var scratch = PdfDoc.CreateNew();
                    scratch.ImportPages(source, chunk, 0);
                    using var fonts = new DocFonts(scratch);
                    prepare?.Invoke(fonts, chunk);
                    var changed = new List<(int, int)>();
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        using var page = scratch.LoadPage(i);
                        if (edit(page, chunk[i], fonts))
                        {
                            page.GenerateContent();
                            changed.Add((chunk[i], i));
                        }

                        progress?.Report((start + i + 1.0) / selected.Count * 0.9);
                    }

                    if (changed.Count > 0) batches.Add(new Batch(scratch.Save(), changed));
                }
            }
        }
        finally
        {
            lock (PdfDoc.Sync) source.Dispose();
        }

        return batches;
    }

    private static byte[] Transplant(byte[] pdf, List<Batch> batches)
    {
        using var input = new MemoryStream(pdf, false);
        using var target = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (var batch in batches)
        {
            using var scratchStream = new MemoryStream(batch.Pdf, false);
            using var scratch = PdfReader.Open(scratchStream, PdfDocumentOpenMode.Import);
            foreach (var (original, index) in batch.Pages)
            {
                var page = target.Pages[original];
                // Importing appends a copy at the end, so the indexes of the real pages stay put.
                var edited = target.AddPage(scratch.Pages[index]);
                Adopt(target, page, edited);
                target.Pages.Remove(edited);
            }
        }

        // Each batch brought its own copy of shared fonts and images; fold them back into one.
        Sharp.Deduplicate(target);
        using var output = new MemoryStream();
        target.Save(output, false);
        return output.ToArray();
    }

    /// <summary>Gives <paramref name="page"/> the content of <paramref name="edited"/> while keeping its own identity.</summary>
    private static void Adopt(PdfDocument document, PdfSharp.Pdf.PdfPage page, PdfSharp.Pdf.PdfPage edited)
    {
        if (edited.Elements["/Contents"] is { } contents) page.Elements["/Contents"] = contents;
        else page.Elements.Remove("/Contents");

        var before = page.Elements.GetDictionary("/Resources");
        var after = edited.Elements.GetDictionary("/Resources");

        // A fresh dictionary: the old one may be shared with other pages and must not change under them.
        var merged = new PdfDictionary(document);
        if (before != null)
            foreach (var key in before.Elements.KeyNames)
                merged.Elements[key.Value] = before.Elements[key.Value];

        foreach (var category in ResourceCategories)
        {
            var oldEntries = before?.Elements.GetDictionary(category);
            var newEntries = after?.Elements.GetDictionary(category);
            if (newEntries == null)
            {
                // Nothing of this kind is used any more.
                merged.Elements.Remove(category);
                continue;
            }

            var entries = new PdfDictionary(document);
            foreach (var name in newEntries.Elements.KeyNames)
            {
                // A resource the page already had keeps pointing at the original object (no duplicate fonts or images);
                // only what the edit added comes from the scratch copy. Names the edit dropped are left out.
                var original = oldEntries?.Elements[name.Value];
                entries.Elements[name.Value] = original ?? newEntries.Elements[name.Value];
            }

            merged.Elements[category] = entries;
        }

        page.Elements["/Resources"] = merged;
    }
}
