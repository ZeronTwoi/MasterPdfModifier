using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PdfModifier.Core.Operations;
using SkiaSharp;

namespace PdfModifier.Services;

public static class BitmapConverter
{
    /// <summary>
    /// Copies a Skia bitmap into a bitmap Avalonia can display. Pages come from the PDF renderer as opaque BGRA and
    /// are copied as they are; pictures with straight alpha are premultiplied on the way, which is what the screen wants.
    /// </summary>
    public static unsafe WriteableBitmap ToAvalonia(SKBitmap source)
    {
        SKBitmap? converted = null;
        if (source.ColorType != SKColorType.Bgra8888 || source.AlphaType == SKAlphaType.Unpremul)
        {
            converted = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888,
                source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul));
            using var pixmap = source.PeekPixels();
            pixmap.ReadPixels(converted.Info, converted.GetPixels(), converted.RowBytes);
            source = converted;
        }

        try
        {
            var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var buffer = bitmap.Lock();
            var rowBytes = Math.Min(buffer.RowBytes, source.RowBytes);
            for (var y = 0; y < source.Height; y++)
                Buffer.MemoryCopy((byte*)source.GetPixels() + (long)y * source.RowBytes,
                    (byte*)buffer.Address + (long)y * buffer.RowBytes, buffer.RowBytes, rowBytes);
            return bitmap;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// A picture for the screen, reduced so its longer side is at most <paramref name="maximum"/> pixels. Photographs
    /// straight from a camera would otherwise take tens of megabytes just to be shown at a fraction of their size.
    /// </summary>
    public static WriteableBitmap ToAvalonia(SKBitmap source, int maximum)
    {
        var longer = Math.Max(source.Width, source.Height);
        if (longer <= maximum) return ToAvalonia(source);
        var factor = (double)maximum / longer;
        var info = new SKImageInfo(Math.Max(1, (int)(source.Width * factor)), Math.Max(1, (int)(source.Height * factor)),
            SKColorType.Bgra8888, source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        using var small = new SKBitmap(info);
        source.ScalePixels(small, SKFilterQuality.Medium);
        return ToAvalonia(small);
    }

    /// <summary>A small picture of an image file, or null when the file is missing or not an image.</summary>
    public static WriteableBitmap? Thumbnail(string path, int size)
    {
        try
        {
            using var decoded = PdfImages.Decode(File.ReadAllBytes(path));
            return ToAvalonia(decoded, size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Core.Pdfium.PdfException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The colour (opaque ARGB) of one pixel of a bitmap made by <see cref="ToAvalonia(SKBitmap)"/>.</summary>
    public static unsafe uint? PixelAt(Bitmap? bitmap, double fractionX, double fractionY)
    {
        if (bitmap is not WriteableBitmap writeable) return null;
        using var buffer = writeable.Lock();
        var x = (int)Math.Clamp(fractionX * buffer.Size.Width, 0, buffer.Size.Width - 1);
        var y = (int)Math.Clamp(fractionY * buffer.Size.Height, 0, buffer.Size.Height - 1);
        var pixel = *(uint*)((byte*)buffer.Address + (long)y * buffer.RowBytes + x * 4);
        return pixel | 0xFF000000;
    }
}
