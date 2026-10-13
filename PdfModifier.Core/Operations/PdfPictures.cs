using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

/// <summary>How a picture is written when it is taken out of a document.</summary>
public enum PictureFormat
{
    /// <summary>A JPEG stays the JPEG it is, byte for byte; everything else becomes a PNG, which loses nothing.</summary>
    AsStored,
    Png,
    Jpeg
}

/// <summary>
/// A picture a page holds: its page, its place among the pictures of that page, where it shows (visual
/// coordinates), how many pixels it has, whether it is stored as a JPEG, and a mark by which the same picture is
/// known again when another page uses it too.
/// </summary>
public sealed record PagePicture(int Page, int Index, RectD Bounds, int Width, int Height, bool Jpeg, string Mark);

/// <param name="Smallest">Pictures that are smaller than this both ways (pixels) are left out: bullets, rules, textures.</param>
/// <param name="Once">A picture that several pages use (a logo, a letterhead) is taken out once, not once per page.</param>
public sealed record PictureOptions(int Smallest = 0, PictureFormat Format = PictureFormat.AsStored, int JpegQuality = 90, bool Once = true);

/// <summary>
/// The pictures a document is made with, at the resolution they are stored in, which is usually far more than a
/// page shows of them. A scanned page is one picture: taking it out gives the scan itself.
/// </summary>
public static unsafe class PdfPictures
{
    /// <summary>The pictures of the chosen pages, page by page in the order the pages draw them.</summary>
    public static IReadOnlyList<PagePicture> In(byte[] pdf, IReadOnlyCollection<int>? pages, int smallest = 0, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        var found = new List<PagePicture>();
        PdfDoc doc;
        lock (PdfDoc.Sync) doc = PdfDoc.Load(pdf);
        try
        {
            List<int> chosen;
            lock (PdfDoc.Sync) chosen = PdfPages.ResolvePages(pages, doc.PageCount);
            for (var i = 0; i < chosen.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                // The lock is taken per page so the page view can keep drawing during a long job.
                lock (PdfDoc.Sync)
                {
                    using var page = doc.LoadPage(chosen[i]);
                    found.AddRange(On(page, smallest));
                }

                progress?.Report((i + 1.0) / chosen.Count);
            }
        }
        finally
        {
            lock (PdfDoc.Sync) doc.Dispose();
        }

        return found;
    }

    /// <summary>
    /// Takes the pictures of the chosen pages out and hands each to <paramref name="write"/> with the ending its file
    /// name needs. Returns how many were handed over.
    /// </summary>
    public static int Extract(byte[] pdf, IReadOnlyCollection<int>? pages, PictureOptions options, Action<PagePicture, byte[], string> write,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var written = 0;
        var seen = new HashSet<string>();
        PdfDoc doc;
        lock (PdfDoc.Sync) doc = PdfDoc.Load(pdf);
        try
        {
            List<int> chosen;
            lock (PdfDoc.Sync) chosen = PdfPages.ResolvePages(pages, doc.PageCount);
            for (var i = 0; i < chosen.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var files = new List<(PagePicture Picture, byte[] Data, string Ending)>();
                lock (PdfDoc.Sync)
                {
                    using var page = doc.LoadPage(chosen[i]);
                    Walk(page, (obj, bounds, index) =>
                    {
                        if (Describe(page.Index, index, obj, bounds, options.Smallest) is not { } picture) return;
                        if (options.Once && !seen.Add(picture.Mark)) return;
                        if (Encode(obj, picture, options) is { } file) files.Add((picture, file.Data, file.Ending));
                    });
                }

                // Written outside the lock: the disk may be slow, the page view should not be.
                foreach (var (picture, data, ending) in files)
                {
                    write(picture, data, ending);
                    written++;
                }

                progress?.Report((i + 1.0) / chosen.Count);
            }
        }
        finally
        {
            lock (PdfDoc.Sync) doc.Dispose();
        }

        return written;
    }

    /// <summary>One picture as a file: its bytes and the ending its name needs. Null when it cannot be read.</summary>
    public static (byte[] Data, string Ending)? AsFile(byte[] pdf, PagePicture picture, PictureOptions options)
    {
        (byte[], string)? file = null;
        Find(pdf, picture, obj => file = Encode(obj, picture, options));
        return file;
    }

    /// <summary>The pixels of one picture, for putting it on the clipboard. The caller owns the result.</summary>
    public static SKBitmap? Pixels(byte[] pdf, PagePicture picture)
    {
        SKBitmap? pixels = null;
        Find(pdf, picture, obj => pixels = ImageObjects.GetPixels(obj));
        return pixels;
    }

    private static void Find(byte[] pdf, PagePicture picture, Action<nint> found)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            if (picture.Page < 0 || picture.Page >= doc.PageCount) return;
            using var page = doc.LoadPage(picture.Page);
            Walk(page, (obj, _, index) =>
            {
                if (index == picture.Index) found(obj);
            });
        }
    }

    /// <summary>The pictures of one page that is loaded already.</summary>
    internal static List<PagePicture> On(PdfPage page, int smallest)
    {
        var found = new List<PagePicture>();
        Walk(page, (obj, bounds, index) =>
        {
            if (Describe(page.Index, index, obj, bounds, smallest) is { } picture) found.Add(picture);
        });
        return found;
    }

    /// <summary>
    /// Goes through the pictures of a page in the order it draws them, those inside nested graphics included, and
    /// names each with where it shows and its place among all pictures of the page.
    /// </summary>
    private static void Walk(PdfPage page, Action<nint, RectD, int> found)
    {
        var index = 0;
        var pageBox = new RectD(0, 0, page.Width, page.Height);
        var toVisual = page.PageToVisual;
        foreach (var obj in page.GetObjects()) Visit(obj, toVisual, 0);

        void Visit(nint obj, Affine toVisualHere, int depth)
        {
            var type = Native.FPDFPageObj_GetType(obj);
            if (type == Native.FPDF_PAGEOBJ_IMAGE)
            {
                float l, b, r, t;
                var bounds = Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 ? toVisualHere.MapBounds(l, b, r, t) : pageBox;
                found(obj, bounds, index++);
            }
            else if (type == Native.FPDF_PAGEOBJ_FORM && depth < 6)
            {
                // What is inside a nested graphic is placed by the graphic's own matrix first.
                FS_MATRIX m;
                var inner = Native.FPDFPageObj_GetMatrix(obj, &m) != 0 ? new Affine(m.a, m.b, m.c, m.d, m.e, m.f).Then(toVisualHere) : toVisualHere;
                var count = Native.FPDFFormObj_CountObjects(obj);
                for (var i = 0; i < count; i++)
                {
                    var child = Native.FPDFFormObj_GetObject(obj, new CULong((uint)i));
                    if (child != 0) Visit(child, inner, depth + 1);
                }
            }
        }
    }

    private static PagePicture? Describe(int page, int index, nint obj, RectD bounds, int smallest)
    {
        uint width, height;
        if (Native.FPDFImageObj_GetImagePixelSize(obj, &width, &height) == 0 || width == 0 || height == 0) return null;
        if (width < smallest && height < smallest) return null;
        var raw = Raw(obj);
        if (raw.Length == 0) return null;
        return new PagePicture(page, index, bounds, (int)width, (int)height, IsJpeg(obj),
            $"{width}x{height}-{Convert.ToHexString(SHA256.HashData(raw), 0, 12)}");
    }

    /// <summary>The picture as it lies in the file, still packed the way it was stored.</summary>
    private static byte[] Raw(nint obj)
    {
        var length = (long)Native.FPDFImageObj_GetImageDataRaw(obj, null, new CULong(0)).Value;
        if (length is <= 0 or > int.MaxValue) return [];
        var data = new byte[length];
        fixed (byte* p = data) Native.FPDFImageObj_GetImageDataRaw(obj, p, new CULong((uint)length));
        return data;
    }

    /// <summary>True when what lies in the file is a JPEG file and nothing else around it.</summary>
    private static bool IsJpeg(nint obj)
    {
        if (Native.FPDFImageObj_GetImageFilterCount(obj) != 1) return false;
        var name = stackalloc byte[64];
        var length = (int)Native.FPDFImageObj_GetImageFilter(obj, 0, name, new CULong(64)).Value;
        return length is > 1 and <= 64 && Encoding.ASCII.GetString(name, length - 1) == "DCTDecode";
    }

    private static (byte[] Data, string Ending)? Encode(nint obj, PagePicture picture, PictureOptions options)
    {
        if (options.Format == PictureFormat.AsStored && picture.Jpeg && Raw(obj) is { Length: > 3 } jpeg && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
            return (jpeg, ".jpg");
        using var pixels = ImageObjects.GetPixels(obj);
        if (pixels == null) return null;
        return options.Format == PictureFormat.Jpeg
            ? (PdfImages.Encode(pixels, ImageFormat.Jpeg, options.JpegQuality), ".jpg")
            : (PdfImages.Encode(pixels, ImageFormat.Png), ".png");
    }
}
