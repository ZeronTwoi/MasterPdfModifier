using System.Runtime.InteropServices;
using System.Text;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

public sealed record CompressOptions
{
    /// <summary>JPEG quality (1-100) for recompressed images.</summary>
    public int JpegQuality { get; init; } = 70;

    /// <summary>Images with a higher effective resolution are downsampled to this. 0 disables downsampling.</summary>
    public int MaxImageDpi { get; init; } = 150;

    public bool Grayscale { get; init; }
    public bool RemoveMetadata { get; init; }
}

public sealed record CompressResult(byte[] Pdf, long OriginalSize, long NewSize, int ImagesRecompressed)
{
    public double SavedRatio => OriginalSize == 0 ? 0 : 1 - (double)NewSize / OriginalSize;
}

public static unsafe class PdfCompress
{
    /// <summary>
    /// Shrinks a document by recompressing/downsampling its images and dropping unreferenced objects.
    /// When nothing can be gained the original bytes are returned unchanged.
    /// </summary>
    public static CompressResult Compress(byte[] pdf, CompressOptions? options = null, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        options ??= new CompressOptions();
        var counts = new Dictionary<int, int>();
        var saved = ContentEditor.Run(pdf, null, (page, index, _) =>
        {
            var changed = 0;
            foreach (var obj in page.GetObjects())
                if (Native.FPDFPageObj_GetType(obj) == Native.FPDF_PAGEOBJ_IMAGE && RecompressImage(page, obj, options))
                    changed++;
            counts[index] = changed;
            return changed > 0;
        }, null, progress == null ? null : new ScaledProgress(progress, 0.9), cancel);

        var recompressed = counts.Values.Sum();
        var result = ImageObjects.DropReplacedImages(saved);
        if (options.RemoveMetadata) result = PdfMetadataOps.RemoveAll(result);
        progress?.Report(1);
        return result.Length < pdf.Length
            ? new CompressResult(result, pdf.Length, result.Length, recompressed)
            : new CompressResult(pdf, pdf.Length, pdf.Length, 0);
    }

    private sealed class ScaledProgress(IProgress<double> inner, double factor) : IProgress<double>
    {
        public void Report(double value) => inner.Report(value * factor);
    }

    private static bool RecompressImage(PdfPage page, nint obj, CompressOptions options)
    {
        if (!ImageObjects.IsRecompressible(page, obj, out var meta, out var rawSize)) return false;

        float l, b, r, t;
        if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) return false;
        double widthPt = Math.Max(1, r - l), heightPt = Math.Max(1, t - b);
        var dpi = Math.Max(meta.width / (widthPt / 72), meta.height / (heightPt / 72));

        using var source = ImageObjects.GetPixels(obj);
        if (source == null) return false;

        var scale = options.MaxImageDpi > 0 && dpi > options.MaxImageDpi * 1.15 ? options.MaxImageDpi / dpi : 1;
        int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));

        var working = source;
        SKBitmap? resized = null, gray = null;
        try
        {
            if (scale < 1) working = resized = ImageFilters.Resize(source, w, h);
            if (options.Grayscale && working.ColorType != SKColorType.Gray8)
                working = gray = PlanarImage.FromBitmap(working, forceGray: true).ToBitmap();

            var jpeg = ImageFilters.EncodeJpeg(working, options.JpegQuality);
            // Replacing is destructive, so only do it for a clear win.
            if (jpeg.Length >= rawSize * 0.9) return false;
            PageDraw.SetJpeg(page, obj, jpeg);
            return true;
        }
        finally
        {
            resized?.Dispose();
            gray?.Dispose();
        }
    }
}

/// <summary>Shared helpers for reading and replacing the pixels of image page objects.</summary>
internal static unsafe class ImageObjects
{
    /// <summary>
    /// True for images that can safely be re-encoded: not bilevel, not transparent, not tiny, and not already stored
    /// with a bilevel codec that JPEG would only make bigger.
    /// </summary>
    public static bool IsRecompressible(PdfPage page, nint obj, out FPDF_IMAGEOBJ_METADATA meta, out long rawSize)
    {
        rawSize = 0;
        FPDF_IMAGEOBJ_METADATA m;
        var ok = Native.FPDFImageObj_GetImageMetadata(obj, page.Handle, &m) != 0;
        meta = m;
        if (!ok || m.width < 48 || m.height < 48 || m.bits_per_pixel <= 1) return false;
        if (Native.FPDFPageObj_HasTransparency(obj) != 0) return false;

        var filterCount = Native.FPDFImageObj_GetImageFilterCount(obj);
        var name = stackalloc byte[64];
        for (var i = 0; i < filterCount; i++)
        {
            var length = (int)Native.FPDFImageObj_GetImageFilter(obj, i, name, new CULong(64)).Value;
            if (length is <= 1 or > 64) continue;
            var filter = Encoding.ASCII.GetString(name, length - 1);
            if (filter is "JBIG2Decode" or "CCITTFaxDecode") return false;
        }

        rawSize = (long)Native.FPDFImageObj_GetImageDataRaw(obj, null, new CULong(0)).Value;
        return rawSize > 0;
    }

    public static SKBitmap? GetPixels(nint obj)
    {
        var native = Native.FPDFImageObj_GetBitmap(obj);
        if (native == 0) return null;
        try
        {
            return PageDraw.ToSkBitmap(native);
        }
        finally
        {
            Native.FPDFBitmap_Destroy(native);
        }
    }

    /// <summary>
    /// PDFium writes a replaced image as a new stream and leaves the old one behind, still listed in the page resources.
    /// This unlists XObjects no content stream draws any more, folds identical images and fonts into one copy and
    /// garbage-collects the file.
    /// </summary>
    public static byte[] DropReplacedImages(byte[] pdf)
    {
        try
        {
            var result = Sharp.Rewrite(pdf, doc =>
            {
                UnusedResources.Prune(doc);
                Sharp.Deduplicate(doc);
            });
            return result.Length < pdf.Length ? result : pdf;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return pdf;
        }
    }
}
