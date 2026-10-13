using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

public enum ImageFormat
{
    Png,
    Jpeg,
    Webp
}

/// <summary>
/// How an image becomes a page. Without a page size the page takes the image's own size at <see cref="Dpi"/>; a
/// resolution of zero is worked out per image (see <see cref="PdfImages.GuessDpi"/>).
/// </summary>
public sealed record ImagePageOptions(double? PageWidth = null, double? PageHeight = null, double Dpi = 150, double Margin = 0)
{
    /// <summary>Each image becomes a page of about the size of a sheet of paper, whatever its pixel count.</summary>
    public static ImagePageOptions Natural { get; } = new(Dpi: 0);
}

/// <summary>Pages to images and images to pages.</summary>
public static class PdfImages
{
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Webp => ".webp",
        _ => ".png"
    };

    public static byte[] Encode(SKBitmap bitmap, ImageFormat format, int quality = 90)
    {
        var skFormat = format switch
        {
            ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            ImageFormat.Webp => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };
        using var data = bitmap.Encode(skFormat, Math.Clamp(quality, 1, 100))
                         ?? throw new PdfException("Could not encode the image.");
        return data.ToArray();
    }

    /// <summary>Renders each selected page and hands the encoded image to <paramref name="sink"/> (page index, bytes).</summary>
    public static void ExportPages(byte[] pdf, IReadOnlyCollection<int>? pages, double dpi, ImageFormat format, int quality,
        Action<int, byte[]> sink, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        using var view = new PdfView(pdf);
        var selected = PdfPages.ResolvePages(pages, view.PageCount);
        for (var i = 0; i < selected.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            using var bitmap = view.Render(selected[i], dpi / 72.0);
            sink(selected[i], Encode(bitmap, format, quality));
            progress?.Report((i + 1.0) / selected.Count);
        }
    }

    /// <summary>
    /// Adds one page per image before the page at <paramref name="atIndex"/>. Pass a null document to build a new PDF from images.
    /// </summary>
    public static byte[] InsertImages(byte[]? pdf, IReadOnlyList<byte[]> images, int atIndex, ImagePageOptions? options = null) =>
        InsertImages(pdf, images.Count, i => images[i], atIndex, options);

    /// <summary>
    /// As <see cref="InsertImages(byte[],IReadOnlyList{byte[]},int,ImagePageOptions)"/> for image files, which are
    /// read one at a time: a folder of photographs does not have to fit into memory at once.
    /// </summary>
    public static byte[] InsertImageFiles(byte[]? pdf, IReadOnlyList<string> files, int atIndex, ImagePageOptions? options = null) =>
        InsertImages(pdf, files.Count, i => File.ReadAllBytes(files[i]), atIndex, options);

    private static byte[] InsertImages(byte[]? pdf, int imageCount, Func<int, byte[]> image, int atIndex, ImagePageOptions? options)
    {
        options ??= new ImagePageOptions();
        if (imageCount == 0) throw new ArgumentException("No images given.", nameof(imageCount));
        lock (PdfDoc.Sync)
        {
            using var doc = pdf == null ? PdfDoc.CreateNew() : PdfDoc.Load(pdf);
            atIndex = Math.Clamp(atIndex, 0, doc.PageCount);
            // New pages are built in small scratch documents and then moved over: writing page content gets slower
            // the more pages the document it happens in already has.
            const int batch = 64;
            for (var start = 0; start < imageCount; start += batch)
            {
                using var scratch = PdfDoc.CreateNew();
                var count = Math.Min(batch, imageCount - start);
                for (var i = 0; i < count; i++)
                {
                    var picture = ImagePixels.FromFile(image(start + i));
                    using var bitmap = picture.Bitmap;
                    var dpi = options.Dpi > 0 ? options.Dpi : GuessDpi(bitmap.Width, bitmap.Height);
                    double imageW = bitmap.Width * 72.0 / dpi, imageH = bitmap.Height * 72.0 / dpi;
                    var pageW = options.PageWidth ?? imageW + 2 * options.Margin;
                    var pageH = options.PageHeight ?? imageH + 2 * options.Margin;
                    double availableW = Math.Max(1, pageW - 2 * options.Margin), availableH = Math.Max(1, pageH - 2 * options.Margin);
                    var fit = Math.Min(availableW / imageW, availableH / imageH);
                    double w = imageW * fit, h = imageH * fit;

                    using var page = scratch.NewPage(i, pageW, pageH);
                    var target = new RectD((pageW - w) / 2, (pageH - h) / 2, (pageW + w) / 2, (pageH + h) / 2);
                    PageDraw.DrawPicture(page, picture, target);
                    page.GenerateContent();
                }

                doc.ImportPages(scratch, null, atIndex);
                atIndex += count;
            }

            return doc.Save();
        }
    }

    /// <summary>
    /// The resolution a scan or photograph of this many pixels most likely has: the usual one that makes it come
    /// closest to a sheet of paper. A picture straight from a phone and a scan at 150 dpi then both become pages of
    /// an ordinary size instead of one being a poster next to the other.
    /// </summary>
    public static double GuessDpi(int width, int height)
    {
        const double sheet = 595.0 * 842.0;
        double[] usual = [72, 96, 150, 200, 300, 400, 600];
        return usual.MinBy(dpi => Math.Abs(Math.Log(width * 72.0 / dpi * (height * 72.0 / dpi) / sheet)));
    }

    /// <summary>True for the files <see cref="InsertImages"/> can turn into pages, judged by their name.</summary>
    public static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".webp" or ".bmp" or ".gif" or ".ico";

    /// <summary>Decodes an image file into a BGRA bitmap, applying the EXIF orientation that cameras record.</summary>
    public static SKBitmap Decode(byte[] imageData) => Decode(imageData, out _, out _);

    /// <summary>
    /// As <see cref="Decode(byte[])"/>, also reporting the file format and whether the pixels had to be turned to
    /// stand upright (in which case the file's own bytes no longer match the result).
    /// </summary>
    internal static SKBitmap Decode(byte[] imageData, out SKEncodedImageFormat format, out bool turned)
    {
        using var stream = new SKMemoryStream(imageData);
        using var codec = SKCodec.Create(stream) ?? throw new PdfException("The image format is not supported.");
        format = codec.EncodedFormat;
        turned = false;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888,
            codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new PdfException("The image could not be decoded.");
        }

        var degrees = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0
        };
        // Mirrored orientations are not undone; they are left as they are rather than embedded wrongly as-is.
        turned = codec.EncodedOrigin is not (SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default);
        if (degrees == 0) return bitmap;

        var swap = degrees != 180;
        var rotated = new SKBitmap(new SKImageInfo(swap ? info.Height : info.Width, swap ? info.Width : info.Height,
            info.ColorType, info.AlphaType));
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.DrawBitmap(bitmap, -bitmap.Width / 2f, -bitmap.Height / 2f);
        }

        bitmap.Dispose();
        return rotated;
    }
}
