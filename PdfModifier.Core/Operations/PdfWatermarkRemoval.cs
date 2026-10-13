using System.Runtime.InteropServices;
using System.Text;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Pdfium;

namespace PdfModifier.Core.Operations;

public enum WatermarkKind
{
    /// <summary>A watermark annotation.</summary>
    Annotation,

    /// <summary>Content explicitly tagged as a watermark by the producing application.</summary>
    Tagged,

    /// <summary>Content that repeats across pages and looks like a watermark (transparent, rotated or page-filling).</summary>
    Repeated
}

/// <summary>
/// Something that may be a watermark. <see cref="Confidence"/> is 1 for explicit watermarks and lower for guesses;
/// <see cref="Key"/> identifies it when asking for removal.
/// </summary>
public sealed record WatermarkCandidate(string Key, WatermarkKind Kind, string Description, int PageCount, double Confidence);

/// <summary>How a mark that is part of a scanned page is told apart from the rest of the page.</summary>
public enum ScanMarkKind
{
    /// <summary>The mark is lighter than the writing: gray text across the page, a faint stamp.</summary>
    Light,

    /// <summary>The mark has a colour of its own: a red stamp, a blue logo.</summary>
    Colored,

    /// <summary>The mark sits in a fixed place and is painted over with the colour of the paper around it.</summary>
    Cover
}

/// <summary>
/// Removal of a mark from the picture of a page. <see cref="Area"/> (visual coordinates) limits the work to one part
/// of the page and is required for <see cref="ScanMarkKind.Cover"/>.
/// </summary>
public sealed record ScanMarkOptions
{
    public ScanMarkKind Kind { get; init; } = ScanMarkKind.Light;

    /// <summary>0-1. For light marks: how dark a mark may be. For coloured marks: how far a tone may be off the colour.</summary>
    public double Strength { get; init; } = 0.5;

    /// <summary>The colour of the mark as it looks on the page (ARGB), for <see cref="ScanMarkKind.Colored"/>.</summary>
    public uint Color { get; init; } = 0xFFC0C0C0;

    public RectD? Area { get; init; }
}

public static unsafe class PdfWatermarkRemoval
{
    private const string AnnotationKey = "annotation";
    private const string TaggedKey = "tagged";

    /// <summary>Confidence from which a candidate is removed without asking.</summary>
    public const double AutoThreshold = 0.75;

    public static IReadOnlyList<WatermarkCandidate> Detect(byte[] pdf, IReadOnlyCollection<int>? pages = null)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            return Analyse(doc, PdfPages.ResolvePages(pages, doc.PageCount)).Candidates;
        }
    }

    /// <summary>
    /// Removes watermarks. With <paramref name="keys"/> null every candidate at or above <see cref="AutoThreshold"/> is removed;
    /// otherwise exactly the listed candidates. Returns the document and how many items were removed.
    /// </summary>
    public static (byte[] Pdf, int Removed) Remove(byte[] pdf, IReadOnlyCollection<int>? pages = null, IReadOnlyCollection<string>? keys = null)
    {
        List<int> selected;
        HashSet<string> chosen;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            selected = PdfPages.ResolvePages(pages, doc.PageCount);
            var analysis = Analyse(doc, selected);
            chosen = (keys ?? analysis.Candidates.Where(c => c.Confidence >= AutoThreshold).Select(c => c.Key).ToList()).ToHashSet();
        }

        if (chosen.Count == 0) return (pdf, 0);
        var current = pdf;
        var removedAnnotations = 0;

        // Annotations live outside the page content, so removing them needs no content rewrite.
        if (chosen.Contains(AnnotationKey))
            current = PdfPages.ForEachPage(current, selected, page =>
            {
                for (var i = Native.FPDFPage_GetAnnotCount(page.Handle) - 1; i >= 0; i--)
                    if (IsWatermarkAnnotation(page, i) && Native.FPDFPage_RemoveAnnot(page.Handle, i) != 0)
                        removedAnnotations++;
            });

        // Counted per page: the editor may run a page a second time when it has to fall back to another strategy.
        var removedObjects = new Dictionary<int, int>();
        current = ContentEditor.Run(current, selected, (page, index, _) =>
        {
            var count = 0;
            var textPage = Native.FPDFText_LoadPage(page.Handle);
            try
            {
                foreach (var obj in page.GetObjects())
                {
                    var key = IsTagged(obj) ? TaggedKey : Signature(page, obj, textPage);
                    if (key == null || !chosen.Contains(key)) continue;
                    page.RemoveObject(obj);
                    count++;
                }
            }
            finally
            {
                if (textPage != 0) Native.FPDFText_ClosePage(textPage);
            }

            removedObjects[index] = count;
            return count > 0;
        });

        var removed = removedAnnotations + removedObjects.Values.Sum();
        return removed == 0 ? (pdf, 0) : (ImageObjects.DropReplacedImages(current), removed);
    }

    /// <summary>
    /// Removes a mark that is part of the page picture itself, as on scans, where there is no separate object to take
    /// away. The pictures of the pages are changed (or, for <see cref="ScanMarkKind.Cover"/>, painted over with a
    /// patch that stays editable). Returns the document and the number of pages changed.
    /// </summary>
    public static (byte[] Pdf, int Pages) RemoveFromScans(byte[] pdf, IReadOnlyCollection<int>? pages, ScanMarkOptions options,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (options.Kind == ScanMarkKind.Cover && options.Area == null) throw new ArgumentException("An area is needed to cover a mark.");
        // Counted per page: the editor may run a page a second time when it has to fall back to another strategy.
        var touched = new Dictionary<int, bool>();
        var result = ContentEditor.Run(pdf, pages, (page, index, _) =>
        {
            var changed = options.Kind == ScanMarkKind.Cover ? Cover(page, options.Area!.Value) : CleanPictures(page, options);
            touched[index] = changed;
            return changed;
        }, null, progress, cancel);

        var count = touched.Values.Count(changed => changed);
        if (count == 0) return (pdf, 0);
        return (options.Kind == ScanMarkKind.Cover ? result : ImageObjects.DropReplacedImages(result), count);
    }

    private static bool Cover(PdfPage page, RectD area)
    {
        var clipped = new RectD(Math.Max(0, area.Left), Math.Max(0, area.Top), Math.Min(page.Width, area.Right), Math.Min(page.Height, area.Bottom));
        if (clipped.Width < 1 || clipped.Height < 1) return false;
        const double scale = 2;
        using var picture = page.Render(scale);
        var (paper, _) = PdfPageObjects.SampleColors(picture, scale, clipped);
        PdfPageObjects.AddPatch(page, clipped, paper);
        return true;
    }

    private static bool CleanPictures(PdfPage page, ScanMarkOptions options)
    {
        var any = false;
        var pageBox = new RectD(0, 0, page.Width, page.Height);
        foreach (var obj in page.GetObjects())
        {
            if (Native.FPDFPageObj_GetType(obj) != Native.FPDF_PAGEOBJ_IMAGE || PdfPageObjects.IsEditable(obj)) continue;
            if (!ImageObjects.IsRecompressible(page, obj, out var meta, out _) || meta.width < 64 || meta.height < 64) continue;
            var bounds = page.GetVisualBounds(obj);
            if (options.Area is { } area &&
                (bounds.Right <= area.Left || bounds.Left >= area.Right || bounds.Bottom <= area.Top || bounds.Top >= area.Bottom)) continue;

            using var source = ImageObjects.GetPixels(obj);
            if (source == null) continue;
            var image = PlanarImage.FromBitmap(source, false);

            Func<int, int, bool>? inside = null;
            if (options.Area is { } limit)
            {
                // From a pixel of the image to where it is shown on the page.
                var toVisual = new Affine(1.0 / image.Width, 0, 0, 1.0 / image.Height, 0, 0).Then(PdfPageObjects.UnitToVisual(page, obj));
                inside = (x, y) =>
                {
                    var (vx, vy) = toVisual.Apply(x + 0.5, y + 0.5);
                    return limit.Contains(vx, vy);
                };
            }

            var changed = options.Kind == ScanMarkKind.Light
                ? ImageFilters.RemoveLightMarks(image, options.Strength, inside)
                : ImageFilters.RemoveColoredMarks(image,
                    [(byte)options.Color, (byte)(options.Color >> 8), (byte)(options.Color >> 16)], options.Strength, inside);
            if (!changed) continue;
            using var cleaned = image.ToBitmap();
            PageDraw.SetJpeg(page, obj, ImageFilters.EncodeJpeg(cleaned, 90));
            any = true;
        }

        return any;
    }

    private sealed class Group
    {
        public string Description = "";
        public readonly HashSet<int> Pages = [];
        public bool Transparent, Rotated, Large, Edge;
    }

    private static (List<WatermarkCandidate> Candidates, int Pages) Analyse(PdfDoc doc, List<int> selected)
    {
        var annotationPages = 0;
        var taggedPages = 0;
        var groups = new Dictionary<string, Group>();

        foreach (var index in selected)
        {
            using var page = doc.LoadPage(index);
            var annotations = Native.FPDFPage_GetAnnotCount(page.Handle);
            for (var i = 0; i < annotations; i++)
                if (IsWatermarkAnnotation(page, i))
                {
                    annotationPages++;
                    break;
                }

            var tagged = false;
            double pageArea = page.Width * page.Height;
            var textPage = Native.FPDFText_LoadPage(page.Handle);
            try
            {
                foreach (var obj in page.GetObjects())
                {
                    if (IsTagged(obj))
                    {
                        tagged = true;
                        continue;
                    }

                    var key = Signature(page, obj, textPage);
                    if (key == null) continue;
                    if (!groups.TryGetValue(key, out var group))
                        groups[key] = group = new Group { Description = Describe(obj, textPage) };
                    group.Pages.Add(index);

                    var bounds = page.GetVisualBounds(obj);
                    FS_MATRIX m;
                    Native.FPDFPageObj_GetMatrix(obj, &m);
                    var scale = Math.Sqrt(Math.Abs(m.a * m.d - m.b * m.c));
                    group.Transparent |= Native.FPDFPageObj_HasTransparency(obj) != 0;
                    group.Rotated |= scale > 0 && Math.Abs(m.b) > 0.15 * scale && Math.Abs(m.a) > 0.15 * scale;
                    group.Large |= bounds.Width * bounds.Height > pageArea * 0.12;
                    group.Edge |= bounds.Bottom < page.Height * 0.1 || bounds.Top > page.Height * 0.9;
                }
            }
            finally
            {
                if (textPage != 0) Native.FPDFText_ClosePage(textPage);
            }

            if (tagged) taggedPages++;
        }

        var candidates = new List<WatermarkCandidate>();
        if (annotationPages > 0)
            candidates.Add(new WatermarkCandidate(AnnotationKey, WatermarkKind.Annotation, "Watermark annotation", annotationPages, 1));
        if (taggedPages > 0)
            candidates.Add(new WatermarkCandidate(TaggedKey, WatermarkKind.Tagged, "Tagged watermark", taggedPages, 1));

        // A watermark sits on (nearly) every page; on a single page there is no repetition to go by.
        var needed = selected.Count == 1 ? 1 : Math.Max(2, (int)Math.Ceiling(selected.Count * 0.6));
        foreach (var (key, group) in groups)
        {
            if (group.Pages.Count < needed) continue;
            var confidence = selected.Count == 1 ? 0.15 : 0.35;
            if (group.Transparent) confidence += 0.3;
            if (group.Rotated) confidence += 0.3;
            if (group.Large) confidence += 0.2;
            if (group.Edge && !group.Large) confidence -= 0.25; // headers, footers and page furniture
            if (confidence < 0.45) continue;
            candidates.Add(new WatermarkCandidate(key, WatermarkKind.Repeated, group.Description, group.Pages.Count, Math.Min(confidence, 0.95)));
        }

        return (candidates.OrderByDescending(c => c.Confidence).ThenByDescending(c => c.PageCount).ToList(), selected.Count);
    }

    private static bool IsWatermarkAnnotation(PdfPage page, int index)
    {
        var annot = Native.FPDFPage_GetAnnot(page.Handle, index);
        if (annot == 0) return false;
        try
        {
            return Native.FPDFAnnot_GetSubtype(annot) == Native.FPDF_ANNOT_WATERMARK;
        }
        finally
        {
            Native.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>True for content marked "Watermark", or as an artifact whose subtype is Watermark (what Acrobat writes).</summary>
    private static bool IsTagged(nint obj)
    {
        var marks = Native.FPDFPageObj_CountMarks(obj);
        var buffer = stackalloc byte[128];
        for (var i = 0; i < marks; i++)
        {
            var mark = Native.FPDFPageObj_GetMark(obj, new CULong((uint)i));
            if (mark == 0) continue;
            CULong length;
            if (Native.FPDFPageObjMark_GetName(mark, buffer, new CULong(128), &length) == 0) continue;
            var name = ReadUtf16(buffer, (int)length.Value);
            if (name == PdfStamps.WatermarkMark) return true;
            if (name != "Artifact") continue;
            if (Native.FPDFPageObjMark_GetParamStringValue(mark, "Subtype", buffer, new CULong(128), &length) != 0 &&
                ReadUtf16(buffer, (int)length.Value) == "Watermark")
                return true;
        }

        return false;
    }

    private static string ReadUtf16(byte* buffer, int byteLength) =>
        byteLength is <= 2 or > 128 ? "" : Encoding.Unicode.GetString(buffer, byteLength - 2);

    /// <summary>
    /// Identity of an object for cross-page matching: its kind, where it sits and what it shows. Paths are ignored;
    /// rules, borders and table lines repeat on every page without being watermarks.
    /// </summary>
    private static string? Signature(PdfPage page, nint obj, nint textPage)
    {
        var type = Native.FPDFPageObj_GetType(obj);
        if (type is not (Native.FPDF_PAGEOBJ_TEXT or Native.FPDF_PAGEOBJ_IMAGE or Native.FPDF_PAGEOBJ_FORM)) return null;
        var b = page.GetVisualBounds(obj);
        if (b.Width < 4 || b.Height < 4) return null;
        var place = $"{Math.Round(b.Left / 2) * 2},{Math.Round(b.Top / 2) * 2},{Math.Round(b.Width / 2) * 2},{Math.Round(b.Height / 2) * 2}";
        switch (type)
        {
            case Native.FPDF_PAGEOBJ_TEXT:
                if (textPage == 0) return null;
                var text = PageDraw.GetObjectText(obj, textPage).Trim();
                return text.Length < 2 ? null : $"T|{place}|{text}";
            case Native.FPDF_PAGEOBJ_IMAGE:
                // The picture itself is part of the identity: a watermark is the same image on every page, whereas the
                // pages of a scanned document are different images that merely sit in the same place.
                return $"I|{place}|{ImageDigest(obj)}";
            default:
                return $"F|{place}|{Native.FPDFFormObj_CountObjects(obj)}";
        }
    }

    private static string ImageDigest(nint image)
    {
        uint width, height;
        Native.FPDFImageObj_GetImagePixelSize(image, &width, &height);
        var size = (int)Native.FPDFImageObj_GetImageDataRaw(image, null, new CULong(0)).Value;
        if (size <= 0) return $"{width}x{height}";
        var data = new byte[size];
        fixed (byte* p = data) Native.FPDFImageObj_GetImageDataRaw(image, p, new CULong((uint)size));
        // A sample of the stored bytes tells different pictures apart; reading every byte of every page would not add anything.
        var hash = 14695981039346656037UL;
        var step = Math.Max(1, size / 4096);
        for (var i = 0; i < size; i += step) hash = (hash ^ data[i]) * 1099511628211UL;
        return $"{width}x{height}:{size}:{hash:X}";
    }

    private static string Describe(nint obj, nint textPage)
    {
        switch (Native.FPDFPageObj_GetType(obj))
        {
            case Native.FPDF_PAGEOBJ_TEXT:
                var text = textPage == 0 ? "" : PageDraw.GetObjectText(obj, textPage).Trim();
                return text.Length > 60 ? text[..60] + "…" : text;
            case Native.FPDF_PAGEOBJ_IMAGE:
                return "Image";
            default:
                return "Graphic";
        }
    }
}
