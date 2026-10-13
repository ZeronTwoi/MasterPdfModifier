using SkiaSharp;

namespace PdfModifier.Core.Imaging;

internal static unsafe class ImageAnalysis
{
    /// <summary>
    /// Bounding box (in pixels, right/bottom exclusive) of everything that differs from the page background,
    /// or null when the page is blank. Expects a BGRA bitmap.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom)? FindContentBounds(SKBitmap bitmap, int tolerance = 24)
    {
        int w = bitmap.Width, h = bitmap.Height;
        if (w < 2 || h < 2) return null;
        var pixels = (uint*)bitmap.GetPixels();
        var stride = bitmap.RowBytes / 4;
        var background = EstimateBorderColor(pixels, w, h, stride);
        int bgB = (int)(background & 0xFF), bgG = (int)((background >> 8) & 0xFF), bgR = (int)((background >> 16) & 0xFF);

        var rows = new int[h];
        var cols = new int[w];
        for (var y = 0; y < h; y++)
        {
            var row = pixels + y * stride;
            for (var x = 0; x < w; x++)
            {
                var p = row[x];
                if (Math.Abs((int)(p & 0xFF) - bgB) > tolerance ||
                    Math.Abs((int)((p >> 8) & 0xFF) - bgG) > tolerance ||
                    Math.Abs((int)((p >> 16) & 0xFF) - bgR) > tolerance)
                {
                    rows[y]++;
                    cols[x]++;
                }
            }
        }

        // A single stray pixel in a row or column is treated as noise.
        const int minimum = 2;
        var top = Array.FindIndex(rows, c => c >= minimum);
        if (top < 0) return null;
        var bottom = Array.FindLastIndex(rows, c => c >= minimum);
        var left = Array.FindIndex(cols, c => c >= minimum);
        var right = Array.FindLastIndex(cols, c => c >= minimum);
        if (left < 0) return null;
        return (left, top, right + 1, bottom + 1);
    }

    internal static uint EstimateBorderColor(uint* pixels, int w, int h, int stride)
    {
        // Most common colour (quantised to 4 bits per channel) along the outer edge.
        var counts = new Dictionary<uint, (int Count, uint Sample)>();

        void Add(uint p)
        {
            var key = p & 0x00F0F0F0;
            counts[key] = counts.TryGetValue(key, out var entry) ? (entry.Count + 1, entry.Sample) : (1, p);
        }

        for (var x = 0; x < w; x++)
        {
            Add(pixels[x]);
            Add(pixels[(h - 1) * stride + x]);
        }

        for (var y = 0; y < h; y++)
        {
            Add(pixels[y * stride]);
            Add(pixels[y * stride + w - 1]);
        }

        return counts.Values.MaxBy(e => e.Count).Sample;
    }

    /// <summary>Median colour of a rectangle's outline and an estimate of the ink colour inside it (ARGB).</summary>
    public static (uint Background, uint Ink) SampleRegionColors(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, bitmap.Width - 1);
        right = Math.Clamp(right, left + 1, bitmap.Width);
        top = Math.Clamp(top, 0, bitmap.Height - 1);
        bottom = Math.Clamp(bottom, top + 1, bitmap.Height);
        var pixels = (uint*)bitmap.GetPixels();
        var stride = bitmap.RowBytes / 4;

        var border = new List<uint>();
        for (var x = left; x < right; x++)
        {
            border.Add(pixels[top * stride + x]);
            border.Add(pixels[(bottom - 1) * stride + x]);
        }

        for (var y = top; y < bottom; y++)
        {
            border.Add(pixels[y * stride + left]);
            border.Add(pixels[y * stride + right - 1]);
        }

        var background = MedianColor(border);
        var bgLuma = Luma(background);

        // Ink = the pixels that contrast most with the background.
        var inside = new List<uint>();
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
            inside.Add(pixels[y * stride + x]);
        inside.Sort((a, b) => Math.Abs(Luma(b) - bgLuma).CompareTo(Math.Abs(Luma(a) - bgLuma)));
        var take = Math.Max(1, inside.Count / 10);
        var ink = MedianColor(inside.GetRange(0, take));
        if (Math.Abs(Luma(ink) - bgLuma) < 40) ink = bgLuma > 128 ? 0xFF000000 : 0xFFFFFFFF;
        return (background | 0xFF000000, ink | 0xFF000000);
    }

    private static int Luma(uint p) => (int)(((p >> 16) & 0xFF) * 299 + ((p >> 8) & 0xFF) * 587 + (p & 0xFF) * 114) / 1000;

    private static uint MedianColor(List<uint> colors)
    {
        var r = colors.Select(c => (c >> 16) & 0xFF).Order().ElementAt(colors.Count / 2);
        var g = colors.Select(c => (c >> 8) & 0xFF).Order().ElementAt(colors.Count / 2);
        var b = colors.Select(c => c & 0xFF).Order().ElementAt(colors.Count / 2);
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }

    /// <summary>Fraction (0-1) of pixels that differ between two BGRA bitmaps; a size mismatch counts the non-overlapping area.</summary>
    public static double DifferenceRatio(SKBitmap a, SKBitmap b, int tolerance = 32)
    {
        int w = Math.Max(a.Width, b.Width), h = Math.Max(a.Height, b.Height);
        int cw = Math.Min(a.Width, b.Width), ch = Math.Min(a.Height, b.Height);
        long different = (long)w * h - (long)cw * ch;
        var pa = (uint*)a.GetPixels();
        var pb = (uint*)b.GetPixels();
        int sa = a.RowBytes / 4, sb = b.RowBytes / 4;
        for (var y = 0; y < ch; y++)
        {
            uint* ra = pa + y * sa, rb = pb + y * sb;
            for (var x = 0; x < cw; x++)
                if (Differs(ra[x], rb[x], tolerance))
                    different++;
        }

        return (double)different / ((long)w * h);
    }

    /// <summary>
    /// How far two BGRA bitmaps of the same size are apart, seen from a distance: both are averaged over squares of
    /// <paramref name="block"/> pixels and the result is the mean difference of those averages, in levels of the
    /// channel that differs most (0-255). Fine print that was sampled a fraction of a pixel differently in the two
    /// leaves this near zero; a picture that is shifted, cut wrongly or of another shade does not.
    /// </summary>
    public static double BlockDifference(SKBitmap a, SKBitmap b, int block = 8)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        var pa = (uint*)a.GetPixels();
        var pb = (uint*)b.GetPixels();
        int sa = a.RowBytes / 4, sb = b.RowBytes / 4;
        double sum = 0;
        long blocks = 0;
        for (var top = 0; top < a.Height; top += block)
        for (var left = 0; left < a.Width; left += block)
        {
            int bottom = Math.Min(a.Height, top + block), right = Math.Min(a.Width, left + block);
            long blue = 0, green = 0, red = 0;
            for (var y = top; y < bottom; y++)
            {
                uint* ra = pa + y * sa, rb = pb + y * sb;
                for (var x = left; x < right; x++)
                {
                    uint p = ra[x], q = rb[x];
                    blue += (int)(p & 0xFF) - (int)(q & 0xFF);
                    green += (int)((p >> 8) & 0xFF) - (int)((q >> 8) & 0xFF);
                    red += (int)((p >> 16) & 0xFF) - (int)((q >> 16) & 0xFF);
                }
            }

            double count = (bottom - top) * (right - left);
            sum += Math.Max(Math.Abs(blue), Math.Max(Math.Abs(green), Math.Abs(red))) / count;
            blocks++;
        }

        return sum / Math.Max(1, blocks);
    }

    public static bool Differs(uint p, uint q, int tolerance) =>
        p != q && (Math.Abs((int)(p & 0xFF) - (int)(q & 0xFF)) > tolerance ||
                   Math.Abs((int)((p >> 8) & 0xFF) - (int)((q >> 8) & 0xFF)) > tolerance ||
                   Math.Abs((int)((p >> 16) & 0xFF) - (int)((q >> 16) & 0xFF)) > tolerance);

    public static int LumaOf(uint p) => Luma(p);
}
