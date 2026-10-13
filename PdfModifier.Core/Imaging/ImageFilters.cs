using SkiaSharp;

namespace PdfModifier.Core.Imaging;

/// <summary>Planar 8-bit image: one plane for grayscale, three (B, G, R) for colour.</summary>
internal sealed class PlanarImage(int width, int height, byte[][] planes)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[][] Planes { get; private set; } = planes;
    public bool IsGray => Planes.Length == 1;

    public static unsafe PlanarImage FromBitmap(SKBitmap bitmap, bool forceGray)
    {
        int w = bitmap.Width, h = bitmap.Height;
        if (bitmap.ColorType == SKColorType.Gray8)
        {
            var gray = new byte[w * h];
            var src = (byte*)bitmap.GetPixels();
            for (var y = 0; y < h; y++)
                new ReadOnlySpan<byte>(src + y * bitmap.RowBytes, w).CopyTo(gray.AsSpan(y * w, w));
            return new PlanarImage(w, h, [gray]);
        }

        if (bitmap.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException("Expected a Gray8 or Bgra8888 bitmap.", nameof(bitmap));

        var pixels = (uint*)bitmap.GetPixels();
        var stride = bitmap.RowBytes / 4;
        var isGray = forceGray || LooksGray(pixels, w, h, stride);
        if (isGray)
        {
            var gray = new byte[w * h];
            Parallel.For(0, h, y =>
            {
                var row = pixels + y * stride;
                var o = y * w;
                for (var x = 0; x < w; x++)
                {
                    var p = row[x];
                    gray[o + x] = (byte)((((p >> 16) & 0xFF) * 299 + ((p >> 8) & 0xFF) * 587 + (p & 0xFF) * 114) / 1000);
                }
            });
            return new PlanarImage(w, h, [gray]);
        }

        byte[] b = new byte[w * h], g = new byte[w * h], r = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            var row = pixels + y * stride;
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                var p = row[x];
                b[o + x] = (byte)p;
                g[o + x] = (byte)(p >> 8);
                r[o + x] = (byte)(p >> 16);
            }
        });
        return new PlanarImage(w, h, [b, g, r]);
    }

    private static unsafe bool LooksGray(uint* pixels, int w, int h, int stride)
    {
        // Sample a grid; a scan of a black-and-white page stored as RGB has near-equal channels everywhere.
        int stepX = Math.Max(1, w / 64), stepY = Math.Max(1, h / 64);
        for (var y = 0; y < h; y += stepY)
        for (var x = 0; x < w; x += stepX)
        {
            var p = pixels[y * stride + x];
            int b = (int)(p & 0xFF), g = (int)((p >> 8) & 0xFF), r = (int)((p >> 16) & 0xFF);
            if (Math.Abs(r - g) > 10 || Math.Abs(g - b) > 10 || Math.Abs(r - b) > 10) return false;
        }

        return true;
    }

    public unsafe SKBitmap ToBitmap()
    {
        int w = Width, h = Height;
        if (IsGray)
        {
            var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
            var dst = (byte*)bitmap.GetPixels();
            var gray = Planes[0];
            for (var y = 0; y < h; y++)
                gray.AsSpan(y * w, w).CopyTo(new Span<byte>(dst + y * bitmap.RowBytes, w));
            return bitmap;
        }

        var color = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = (uint*)color.GetPixels();
        var stride = color.RowBytes / 4;
        byte[] b = Planes[0], g = Planes[1], r = Planes[2];
        Parallel.For(0, h, y =>
        {
            var row = pixels + y * stride;
            var o = y * w;
            for (var x = 0; x < w; x++)
                row[x] = 0xFF000000u | ((uint)r[o + x] << 16) | ((uint)g[o + x] << 8) | b[o + x];
        });
        return color;
    }

    /// <summary>Luminance plane (the gray plane itself, or a weighted mix of the colour planes).</summary>
    public byte[] Luminance()
    {
        if (IsGray) return Planes[0];
        byte[] b = Planes[0], g = Planes[1], r = Planes[2];
        var result = new byte[b.Length];
        Parallel.For(0, Height, y =>
        {
            var o = y * Width;
            for (var i = o; i < o + Width; i++) result[i] = (byte)((r[i] * 299 + g[i] * 587 + b[i] * 114) / 1000);
        });
        return result;
    }

    public void ReplacePlanes(byte[][] planes) => Planes = planes;
}

internal static class ImageFilters
{
    /// <summary>
    /// Stretches levels so the paper becomes white and the ink black. Returns false (and changes nothing) when the image
    /// has no dominant bright background, e.g. a photograph. <paramref name="strength"/> (0-1) pushes the white point lower,
    /// which also wipes out faint marks such as light watermarks.
    /// </summary>
    public static bool WhitenBackground(PlanarImage image, double strength = 0.3)
    {
        var histogram = new long[256];
        foreach (var v in image.Luminance()) histogram[v]++;
        if (EstimateLevels(histogram, strength) is not var (black, white)) return false;
        ApplyLevels(image, black, white);
        return true;
    }

    /// <summary>
    /// The tones that should become black and white, from a histogram of the page's brightness. Null when there is no
    /// dominant bright background or nothing would change.
    /// </summary>
    public static (int Black, int White)? EstimateLevels(long[] histogram, double strength)
    {
        long total = histogram.Sum(), bright = 0;
        for (var i = 128; i < 256; i++) bright += histogram[i];
        if (bright < total / 2) return null;

        var peak = 128;
        for (var i = 129; i < 256; i++)
            if (histogram[i] > histogram[peak])
                peak = i;

        // Width of the paper peak on its dark side.
        var edge = peak;
        while (edge > 100 && histogram[edge] > histogram[peak] / 4) edge--;
        var spread = peak - edge;

        long accumulated = 0;
        var black = 0;
        while (black < 100 && accumulated + histogram[black] < total / 200) accumulated += histogram[black++];

        var white = (int)(peak - spread * 0.5 - Math.Clamp(strength, 0, 1) * 70);
        white = Math.Max(white, black + 40);
        if (white >= 254 && black <= 1) return null;
        return (black, white);
    }

    public static void ApplyLevels(PlanarImage image, int black, int white)
    {
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp((i - black) * 255.0 / (white - black), 0, 255);

        foreach (var plane in image.Planes)
            Parallel.For(0, image.Height, y =>
            {
                var o = y * image.Width;
                for (var i = o; i < o + image.Width; i++) plane[i] = lut[plane[i]];
            });
    }

    /// <summary>
    /// Evens out the lighting of a page: shadows, a darker edge where the sheet did not lie flat, yellowed paper.
    /// The paper becomes white everywhere while the writing keeps its darkness relative to the paper around it.
    /// Returns a new grayscale image; the input is not changed.
    /// </summary>
    public static PlanarImage FlattenBackground(PlanarImage image)
    {
        int w = image.Width, h = image.Height;
        var luma = image.Luminance();

        // How bright the paper is around each spot: the brightest tones of a cell much larger than a letter.
        var cell = Math.Max(8, Math.Max(w, h) / 90);
        int gw = (w + cell - 1) / cell, gh = (h + cell - 1) / cell;
        var grid = new float[gw * gh];
        Parallel.For(0, gh, gy =>
        {
            Span<int> histogram = stackalloc int[256];
            for (var gx = 0; gx < gw; gx++)
            {
                histogram.Clear();
                int x1 = Math.Min(w, (gx + 1) * cell), y1 = Math.Min(h, (gy + 1) * cell), count = 0;
                for (var y = gy * cell; y < y1; y++)
                for (var x = gx * cell; x < x1; x++, count++)
                    histogram[luma[y * w + x]]++;
                // Not the very brightest pixel, which may be a speck of glare, but nearly.
                var skip = count / 10;
                var level = 255;
                while (level > 0 && (skip -= histogram[level]) >= 0) level--;
                grid[gy * gw + gx] = level;
            }
        });

        // A cell filled by a heading or a blot would count as dark paper; its brighter neighbours know better.
        var spread = new float[grid.Length];
        for (var gy = 0; gy < gh; gy++)
        for (var gx = 0; gx < gw; gx++)
        {
            float best = 0;
            for (var y = Math.Max(0, gy - 1); y <= Math.Min(gh - 1, gy + 1); y++)
            for (var x = Math.Max(0, gx - 1); x <= Math.Min(gw - 1, gx + 1); x++)
                best = Math.Max(best, grid[y * gw + x]);
            spread[gy * gw + gx] = best;
        }

        var smooth = new float[grid.Length];
        for (var gy = 0; gy < gh; gy++)
        for (var gx = 0; gx < gw; gx++)
        {
            float sum = 0;
            var n = 0;
            for (var y = Math.Max(0, gy - 1); y <= Math.Min(gh - 1, gy + 1); y++)
            for (var x = Math.Max(0, gx - 1); x <= Math.Min(gw - 1, gx + 1); x++, n++)
                sum += spread[y * gw + x];
            smooth[gy * gw + gx] = sum / n;
        }

        var result = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            // Between cell centres the paper brightness is interpolated, so no cell borders show.
            var fy = Math.Clamp((y + 0.5f) / cell - 0.5f, 0, gh - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(gh - 1, y0 + 1);
            var ty = fy - y0;
            for (var x = 0; x < w; x++)
            {
                var fx = Math.Clamp((x + 0.5f) / cell - 0.5f, 0, gw - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(gw - 1, x0 + 1);
                var tx = fx - x0;
                var paper = (smooth[y0 * gw + x0] * (1 - tx) + smooth[y0 * gw + x1] * tx) * (1 - ty) +
                            (smooth[y1 * gw + x0] * (1 - tx) + smooth[y1 * gw + x1] * tx) * ty;
                result[y * w + x] = (byte)Math.Min(255, luma[y * w + x] * 255f / Math.Max(24, paper));
            }
        });
        return new PlanarImage(w, h, [result]);
    }

    /// <summary>
    /// The tone of the paper (the commonest bright tone) and of the darkest writing. Null for images without a
    /// bright background, such as photographs.
    /// </summary>
    public static (byte[] Paper, int PaperLevel, int InkLevel)? PaperAndInk(PlanarImage image)
    {
        var luma = image.Luminance();
        var histogram = new long[256];
        foreach (var v in luma) histogram[v]++;
        long total = luma.Length, bright = 0;
        for (var i = 128; i < 256; i++) bright += histogram[i];
        if (bright < total * 0.4) return null;

        var peak = 128;
        for (var i = 129; i < 256; i++)
            if (histogram[i] > histogram[peak])
                peak = i;

        long accumulated = 0;
        var ink = 0;
        while (ink < peak - 20 && accumulated + histogram[ink] < total / 200) accumulated += histogram[ink++];

        // The colour of the paper: the average of the pixels at its tone, plane by plane.
        var sums = new long[image.Planes.Length];
        long count = 0;
        var step = Math.Max(1, luma.Length / 400_000);
        for (var i = 0; i < luma.Length; i += step)
        {
            if (Math.Abs(luma[i] - peak) > 4) continue;
            for (var p = 0; p < sums.Length; p++) sums[p] += image.Planes[p][i];
            count++;
        }

        var paper = sums.Select(sum => (byte)(count == 0 ? peak : sum / count)).ToArray();
        return (paper, peak, ink);
    }

    /// <summary>
    /// Takes away marks that are lighter than the writing: a gray "COPY" across a scan, a faint stamp, show-through
    /// from the back of the sheet. Everything lighter than a level between paper and ink becomes paper; the level
    /// moves towards the ink as <paramref name="strength"/> (0-1) grows. <paramref name="inside"/> limits the change
    /// to part of the image. Returns false when the image has no paper to restore.
    /// </summary>
    public static bool RemoveLightMarks(PlanarImage image, double strength, Func<int, int, bool>? inside = null)
    {
        if (PaperAndInk(image) is not var (paper, paperLevel, inkLevel)) return false;
        var range = Math.Max(1, paperLevel - inkLevel);
        FadeAbove(image, paper, paperLevel - range * (0.12 + 0.68 * Math.Clamp(strength, 0, 1)), Math.Max(6, range * 0.1), inside);
        return true;
    }

    /// <summary>Turns everything lighter than <paramref name="level"/> into paper.</summary>
    private static void FadeAbove(PlanarImage image, byte[] paper, double level, double band, Func<int, int, bool>? inside)
    {
        var luma = image.Luminance();
        var planes = image.Planes;
        var w = image.Width;
        Parallel.For(0, image.Height, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                // Tones just below the level are faded rather than cut, so the edges of letters stay smooth.
                var keep = (level - luma[i]) / band;
                if (keep >= 1 || (inside != null && !inside(x, y))) continue;
                keep = Math.Max(0, keep);
                for (var p = 0; p < planes.Length; p++)
                    planes[p][i] = (byte)(paper[p] + (planes[p][i] - paper[p]) * keep);
            }
        });
    }

    /// <summary>
    /// Takes away marks of one colour: a red stamp, a blue logo printed across the page. A pixel is understood as the
    /// paper with some of the mark's colour over it and some neutral darkness (black or gray writing) in it. The mark's
    /// part is taken out and the writing's part kept, so text the mark runs across comes back as text.
    /// <paramref name="mark"/> is the colour as B, G, R (or one value for gray images); <paramref name="tolerance"/>
    /// (0-1) says how far a pixel may be off that explanation and still be changed. A mark without a colour of its own
    /// cannot be told from writing this way and is removed by its lightness instead.
    /// </summary>
    public static bool RemoveColoredMarks(PlanarImage image, byte[] mark, double tolerance, Func<int, int, bool>? inside = null)
    {
        if (PaperAndInk(image) is not var (paper, paperLevel, inkLevel)) return false;
        tolerance = Math.Clamp(tolerance, 0, 1);
        var planes = image.Planes;

        // Two directions away from the paper colour: towards the mark, and towards black.
        double[] towardsMark = new double[3], towardsBlack = new double[3];
        double mm = 0, mb = 0, bb = 0;
        for (var p = 0; p < 3; p++)
        {
            var sheet = paper[Math.Min(p, paper.Length - 1)];
            towardsMark[p] = (mark.Length == 3 ? mark[p] : mark[0]) - sheet;
            towardsBlack[p] = -sheet;
            mm += towardsMark[p] * towardsMark[p];
            mb += towardsMark[p] * towardsBlack[p];
            bb += towardsBlack[p] * towardsBlack[p];
        }

        var determinant = mm * bb - mb * mb;
        // The two directions have to differ by a clear angle, or "mark" and "writing" are the same thing.
        if (planes.Length < 3 || mm < 12 * 12 || determinant < 0.03 * mm * bb)
        {
            var markLuma = mark.Length == 3 ? (mark[2] * 299 + mark[1] * 587 + mark[0] * 114) / 1000.0 : mark[0];
            if (paperLevel - markLuma < 10) return false;
            // Gray like the writing, only lighter: everything about as light as the mark, or lighter, goes.
            FadeAbove(image, paper, markLuma - 10 - 50 * tolerance, 8, inside);
            return true;
        }

        var allowed = 14 + 70 * tolerance;
        int w = image.Width, h = image.Height;

        // How much of the mark and how much darkness a pixel holds, or false when it is not explained by the two.
        bool Split(int i, out double share, out double darkness)
        {
            double om = 0, ob = 0;
            Span<double> offset = stackalloc double[3];
            for (var p = 0; p < 3; p++)
            {
                offset[p] = planes[p][i] - paper[p];
                om += offset[p] * towardsMark[p];
                ob += offset[p] * towardsBlack[p];
            }

            share = (om * bb - ob * mb) / determinant;
            darkness = (ob * mm - om * mb) / determinant;
            // Down to a faint tint, so no outline of the mark is left where its edge fades into the paper.
            if (share < 0.03 || share > 1.6 || darkness < -0.08) return false;
            double off2 = 0;
            for (var p = 0; p < 3; p++)
            {
                var rest = offset[p] - share * towardsMark[p] - darkness * towardsBlack[p];
                off2 += rest * rest;
            }

            return off2 <= allowed * allowed;
        }

        // A stamp of real ink lets the writing under it show at full darkness. One laid over the page afterwards
        // covers it in part: where the mark is at full strength the writing looks paler than it is, by the same
        // factor everywhere. The darkest writing found under the full mark tells that factor.
        var levels = new int[101];
        Parallel.For(0, h, () => new int[101], (y, _, local) =>
        {
            for (var x = y & 3; x < w; x += 4)
                if (Split(y * w + x, out var share, out var darkness) && share is > 0.8 and < 1.25 && darkness > 0.15)
                    local[(int)Math.Clamp(darkness * 100, 0, 100)]++;
            return local;
        }, local =>
        {
            lock (levels)
                for (var i = 0; i < levels.Length; i++)
                    levels[i] += local[i];
        });

        var fullDarkness = Math.Clamp(1 - (double)inkLevel / Math.Max(1, paperLevel), 0.2, 1);
        var visible = 1.0;
        long counted = levels.Sum(), above = 0;
        if (counted >= 40)
        {
            // The middle of a stroke, fully under the mark, is rare next to all the edges: the darkest hundredth.
            var darkest = 100;
            while (darkest > 0 && (above += levels[darkest]) < Math.Max(4, counted / 100)) darkest--;
            visible = Math.Clamp(darkest / 100.0 / fullDarkness, 0.2, 1);
        }

        var cleared = new bool[w * h];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!Split(i, out var share, out var darkness) || (inside != null && !inside(x, y))) continue;
                // A little darkness is the grain of the mark, not writing: the paper under a mark comes back plain.
                // Along the mark's edge there is more of it (lossy compression blurs colour more than brightness),
                // which would otherwise be left standing as a thin outline.
                var grain = 0.06 + 0.22 * (1 - Math.Min(1, share));
                darkness = darkness < grain ? 0 : darkness < grain + 0.08 ? (darkness - grain) * (grain + 0.08) / 0.08 : darkness;
                // The thinner the mark at this spot, the less of the writing it hid.
                var shown = 1 - Math.Min(1, share) * (1 - visible);
                var keep = 1 - Math.Clamp(darkness / shown, 0, fullDarkness);
                for (var p = 0; p < 3; p++) planes[p][i] = (byte)Math.Clamp(paper[p] * keep, 0, 255);
                cleared[i] = true;
            }
        });

        // Lossy compression leaves a faint ripple just outside a strong edge. Without its mark it would stand as an
        // outline of what was removed, so paper-like pixels right next to cleared ones become plain paper too.
        const int reach = 2;
        var near = new bool[w * h];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var found = false;
                for (var dx = Math.Max(0, x - reach); dx <= Math.Min(w - 1, x + reach) && !found; dx++) found = cleared[y * w + dx];
                near[y * w + x] = found;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (cleared[i]) continue;
                var found = false;
                for (var dy = Math.Max(0, y - reach); dy <= Math.Min(h - 1, y + reach) && !found; dy++) found = near[dy * w + x];
                if (!found || (inside != null && !inside(x, y))) continue;
                var paperLike = true;
                for (var p = 0; p < 3 && paperLike; p++) paperLike = Math.Abs(planes[p][i] - paper[p]) <= 24;
                if (!paperLike) continue;
                for (var p = 0; p < 3; p++) planes[p][i] = paper[p];
            }
        });
        return true;
    }

    /// <summary>Unsharp mask with a 3×3 box blur.</summary>
    public static void Sharpen(PlanarImage image, double amount = 0.8)
    {
        int w = image.Width, h = image.Height;
        if (w < 3 || h < 3) return;
        var planes = new byte[image.Planes.Length][];
        for (var p = 0; p < planes.Length; p++)
        {
            var src = image.Planes[p];
            var dst = (byte[])src.Clone();
            Parallel.For(1, h - 1, y =>
            {
                var o = y * w;
                for (var x = 1; x < w - 1; x++)
                {
                    var i = o + x;
                    var blur = (src[i - w - 1] + src[i - w] + src[i - w + 1] +
                                src[i - 1] + src[i] + src[i + 1] +
                                src[i + w - 1] + src[i + w] + src[i + w + 1]) / 9.0;
                    dst[i] = (byte)Math.Clamp(src[i] + amount * (src[i] - blur) + 0.5, 0, 255);
                }
            });
            planes[p] = dst;
        }

        image.ReplacePlanes(planes);
    }

    /// <summary>
    /// Brings print that is out of focus back into it. Blur spreads every stroke over its surroundings; this works
    /// out what the page must have looked like for the blur to give what is there (Richardson–Lucy, with an even
    /// blur whose width grows with <paramref name="strength"/>, 0-1). Colours are kept: only the brightness is
    /// worked on, and each pixel then gets lighter or darker by as much as its brightness did.
    /// </summary>
    public static void Deblur(PlanarImage image, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        int w = image.Width, h = image.Height;
        if (strength <= 0 || w < 8 || h < 8) return;
        var kernel = GaussianKernel(0.8 + 2.2 * strength);
        var rounds = 6 + (int)Math.Round(12 * strength);
        var luma = image.Luminance();
        var focused = new byte[w * h];

        // In bands, so that a large scan does not need several copies of itself in memory at once. Each band is
        // worked out with some rows beyond it, which are then thrown away: at its edges a band would see only half.
        var bandRows = Math.Max(64, 3_000_000 / w);
        var margin = kernel.Length * 2;
        for (var top = 0; top < h; top += bandRows)
        {
            int from = Math.Max(0, top - margin), to = Math.Min(h, top + bandRows + margin), rows = to - from;
            var observed = new float[w * rows];
            for (var i = 0; i < observed.Length; i++) observed[i] = luma[from * w + i] / 255f;
            var estimate = (float[])observed.Clone();
            float[] work = new float[observed.Length], scratch = new float[observed.Length];
            for (var round = 0; round < rounds; round++)
            {
                Blur(estimate, work, scratch, w, rows, kernel);
                Parallel.For(0, rows, y =>
                {
                    for (int i = y * w, end = i + w; i < end; i++) work[i] = observed[i] / Math.Max(work[i], 0.004f);
                });
                Blur(work, work, scratch, w, rows, kernel);
                Parallel.For(0, rows, y =>
                {
                    for (int i = y * w, end = i + w; i < end; i++) estimate[i] = Math.Clamp(estimate[i] * work[i], 0f, 1f);
                });
            }

            int keepFrom = top - from, keepTo = Math.Min(h, top + bandRows) - from;
            for (var y = keepFrom; y < keepTo; y++)
            for (var x = 0; x < w; x++)
                focused[(from + y) * w + x] = (byte)(estimate[y * w + x] * 255f + 0.5f);
        }

        if (image.IsGray)
        {
            image.ReplacePlanes([focused]);
            return;
        }

        var planes = image.Planes.Select(plane => (byte[])plane.Clone()).ToArray();
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                var change = focused[i] - luma[i];
                if (change == 0) continue;
                foreach (var plane in planes) plane[i] = (byte)Math.Clamp(plane[i] + change, 0, 255);
            }
        });
        image.ReplacePlanes(planes);
    }

    private static float[] GaussianKernel(double sigma)
    {
        var reach = Math.Max(1, (int)Math.Ceiling(sigma * 2.5));
        var kernel = new float[reach * 2 + 1];
        float sum = 0;
        for (var i = -reach; i <= reach; i++) sum += kernel[i + reach] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        return kernel;
    }

    /// <summary>Blurs across and then down. The result may go into the array it was read from.</summary>
    private static void Blur(float[] source, float[] target, float[] scratch, int w, int h, float[] kernel)
    {
        var reach = kernel.Length / 2;
        Parallel.For(0, h, y =>
        {
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                float sum = 0;
                for (var k = -reach; k <= reach; k++) sum += source[o + Math.Clamp(x + k, 0, w - 1)] * kernel[k + reach];
                scratch[o + x] = sum;
            }
        });
        Parallel.For(0, h, y =>
        {
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                float sum = 0;
                for (var k = -reach; k <= reach; k++) sum += scratch[Math.Clamp(y + k, 0, h - 1) * w + x] * kernel[k + reach];
                target[o + x] = sum;
            }
        });
    }

    /// <summary>
    /// Makes thin, pale or broken strokes heavier: every stroke grows by up to a pixel on each side, and tones between
    /// paper and ink are drawn towards the ink. <paramref name="strength"/> is 0-1. Meant for dark print on light paper.
    /// </summary>
    public static void Thicken(PlanarImage image, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        int w = image.Width, h = image.Height;
        if (strength <= 0 || w < 3 || h < 3) return;
        var tone = new byte[256];
        for (var i = 0; i < 256; i++) tone[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, 1 + 0.6 * strength));

        var planes = new byte[image.Planes.Length][];
        for (var p = 0; p < planes.Length; p++)
        {
            var src = image.Planes[p];
            var dst = (byte[])src.Clone();
            Parallel.For(1, h - 1, y =>
            {
                var o = y * w;
                for (var x = 1; x < w - 1; x++)
                {
                    var i = o + x;
                    var darkest = Math.Min(Math.Min(Math.Min(src[i - w - 1], src[i - w]), Math.Min(src[i - w + 1], src[i - 1])),
                        Math.Min(Math.Min(src[i], src[i + 1]), Math.Min(Math.Min(src[i + w - 1], src[i + w]), src[i + w + 1])));
                    dst[i] = tone[(int)(src[i] - strength * (src[i] - darkest) + 0.5)];
                }
            });
            planes[p] = dst;
        }

        image.ReplacePlanes(planes);
    }

    /// <summary>
    /// Evens out the lighting like <see cref="FlattenBackground"/>, but in place and in colour: every pixel becomes
    /// brighter by as much as the paper around it is darker than white.
    /// </summary>
    public static void EvenLight(PlanarImage image)
    {
        var luma = image.Luminance();
        var flat = FlattenBackground(image).Planes[0];
        if (image.IsGray)
        {
            image.ReplacePlanes([flat]);
            return;
        }

        int w = image.Width, h = image.Height;
        var planes = image.Planes.Select(plane => (byte[])plane.Clone()).ToArray();
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                var gain = (flat[i] + 1f) / (luma[i] + 1f);
                foreach (var plane in planes) plane[i] = (byte)Math.Min(255f, plane[i] * gain + 0.5f);
            }
        });
        image.ReplacePlanes(planes);
    }

    /// <summary>3×3 median filter; removes salt-and-pepper speckle.</summary>
    public static void Denoise(PlanarImage image)
    {
        int w = image.Width, h = image.Height;
        if (w < 3 || h < 3) return;
        var planes = new byte[image.Planes.Length][];
        for (var p = 0; p < planes.Length; p++)
        {
            var src = image.Planes[p];
            var dst = (byte[])src.Clone();
            Parallel.For(1, h - 1, y =>
            {
                Span<byte> window = stackalloc byte[9];
                var o = y * w;
                for (var x = 1; x < w - 1; x++)
                {
                    var i = o + x;
                    window[0] = src[i - w - 1];
                    window[1] = src[i - w];
                    window[2] = src[i - w + 1];
                    window[3] = src[i - 1];
                    window[4] = src[i];
                    window[5] = src[i + 1];
                    window[6] = src[i + w - 1];
                    window[7] = src[i + w];
                    window[8] = src[i + w + 1];
                    window.Sort();
                    dst[i] = window[4];
                }
            });
            planes[p] = dst;
        }

        image.ReplacePlanes(planes);
    }

    /// <summary>Sauvola adaptive threshold. The result is a single plane holding only 0 and 255.</summary>
    public static void Binarize(PlanarImage image, int window = 31, double k = 0.2)
    {
        int w = image.Width, h = image.Height;
        var src = image.Luminance();
        var sum = new long[(w + 1) * (h + 1)];
        var sumSq = new long[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            long rowSum = 0, rowSq = 0;
            for (var x = 0; x < w; x++)
            {
                int v = src[y * w + x];
                rowSum += v;
                rowSq += v * v;
                sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + rowSum;
                sumSq[(y + 1) * (w + 1) + x + 1] = sumSq[y * (w + 1) + x + 1] + rowSq;
            }
        }

        var half = window / 2;
        var dst = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Max(0, y - half), y1 = Math.Min(h, y + half + 1);
            for (var x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - half), x1 = Math.Min(w, x + half + 1);
                var n = (y1 - y0) * (x1 - x0);
                var s = sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0];
                var sq = sumSq[y1 * (w + 1) + x1] - sumSq[y0 * (w + 1) + x1] - sumSq[y1 * (w + 1) + x0] + sumSq[y0 * (w + 1) + x0];
                var mean = (double)s / n;
                var deviation = Math.Sqrt(Math.Max(0, (double)sq / n - mean * mean));
                var threshold = mean * (1 + k * (deviation / 128 - 1));
                dst[y * w + x] = src[y * w + x] > threshold ? (byte)255 : (byte)0;
            }
        });
        image.ReplacePlanes([dst]);
    }

    /// <summary>
    /// Estimates how far text lines are tilted, in degrees; positive means the content is rotated clockwise.
    /// Returns 0 when no reliable skew is found.
    /// </summary>
    public static double EstimateSkew(PlanarImage image, double maxAngle = 7)
    {
        // Work on a small copy: skew is a global property and this keeps the search cheap.
        var luma = image.Luminance();
        var step = Math.Max(1, Math.Max(image.Width, image.Height) / 900);
        int w = image.Width / step, h = image.Height / step;
        if (w < 50 || h < 50) return 0;

        var histogram = new long[256];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            histogram[luma[y * step * image.Width + x * step]]++;
        var threshold = OtsuThreshold(histogram);

        var xs = new List<int>();
        var ys = new List<int>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (luma[y * step * image.Width + x * step] < threshold)
            {
                xs.Add(x);
                ys.Add(y);
            }

        if (xs.Count < 200 || xs.Count > w * h * 0.6) return 0;

        double Score(double degrees)
        {
            var tan = Math.Tan(degrees * Math.PI / 180);
            var bins = new int[h + 2 * w + 4];
            for (var i = 0; i < xs.Count; i++)
            {
                var r = (int)Math.Round(ys[i] - xs[i] * tan) + w;
                if ((uint)r < (uint)bins.Length) bins[r]++;
            }

            // Text lines aligned with the projection give sharp jumps between neighbouring bins.
            double score = 0;
            for (var i = 1; i < bins.Length; i++)
            {
                double d = bins[i] - bins[i - 1];
                score += d * d;
            }

            return score;
        }

        double best = 0, bestScore = Score(0);
        var zeroScore = bestScore;
        for (var a = -maxAngle; a <= maxAngle; a += 0.5)
        {
            var s = Score(a);
            if (s > bestScore) (best, bestScore) = (a, s);
        }

        var coarse = best;
        for (var a = coarse - 0.5; a <= coarse + 0.5; a += 0.05)
        {
            var s = Score(a);
            if (s > bestScore) (best, bestScore) = (a, s);
        }

        // Ignore results pinned to the search limit or barely better than "no skew".
        if (Math.Abs(best) > maxAngle - 0.26 || Math.Abs(best) < 0.1 || bestScore < zeroScore * 1.1) return 0;
        return Math.Round(best, 2);
    }

    public static int OtsuThreshold(long[] histogram)
    {
        long total = histogram.Sum();
        double sumAll = 0;
        for (var i = 0; i < 256; i++) sumAll += i * (double)histogram[i];
        double sumBack = 0, weightBack = 0, bestVariance = -1;
        var threshold = 128;
        for (var i = 0; i < 256; i++)
        {
            weightBack += histogram[i];
            if (weightBack == 0) continue;
            var weightFore = total - weightBack;
            if (weightFore <= 0) break;
            sumBack += i * (double)histogram[i];
            var meanBack = sumBack / weightBack;
            var meanFore = (sumAll - sumBack) / weightFore;
            var variance = weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (variance > bestVariance)
            {
                bestVariance = variance;
                threshold = i + 1;
            }
        }

        return threshold;
    }

    /// <summary>Rotates around the centre, keeping the size and filling the exposed corners with white. Positive is clockwise.</summary>
    public static SKBitmap Rotate(SKBitmap source, double degrees)
    {
        var result = new SKBitmap(new SKImageInfo(source.Width, source.Height, source.ColorType, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.RotateDegrees((float)degrees, source.Width / 2f, source.Height / 2f);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }

    public static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var result = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
        return result;
    }

    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }
}
