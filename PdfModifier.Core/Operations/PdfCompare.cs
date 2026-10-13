using PdfModifier.Core.Imaging;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

public enum TextChangeKind
{
    Removed,
    Added
}

/// <summary>A stretch of words present in only one of the two documents.</summary>
public sealed record TextChange(TextChangeKind Kind, string Text);

/// <summary>
/// Differences on one page position. <see cref="PixelDifference"/> is the fraction of the rendered page that differs (0-1).
/// </summary>
public sealed record PageComparison(int PageIndex, bool InFirst, bool InSecond, double PixelDifference, IReadOnlyList<TextChange> TextChanges)
{
    public bool Identical => InFirst && InSecond && PixelDifference < 0.00005 && TextChanges.Count == 0;
}

public sealed record ComparisonResult(int FirstPageCount, int SecondPageCount, IReadOnlyList<PageComparison> Pages)
{
    public bool Identical => FirstPageCount == SecondPageCount && Pages.All(p => p.Identical);
    public int DifferentPages => Pages.Count(p => !p.Identical);
}

public static class PdfCompare
{
    /// <summary>Compares two documents page by page, both visually and by their text.</summary>
    public static ComparisonResult Compare(byte[] first, byte[] second, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        using var a = new PdfView(first);
        using var b = new PdfView(second);
        var count = Math.Max(a.PageCount, b.PageCount);
        var pages = new List<PageComparison>(count);
        for (var i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            bool inA = i < a.PageCount, inB = i < b.PageCount;
            if (inA && inB)
            {
                using var bitmapA = a.Render(i, 1);
                using var bitmapB = b.Render(i, 1);
                pages.Add(new PageComparison(i, true, true, ImageAnalysis.DifferenceRatio(bitmapA, bitmapB),
                    DiffWords(a.GetText(i), b.GetText(i))));
            }
            else
            {
                var text = inA ? a.GetText(i) : b.GetText(i);
                var words = SplitWords(text);
                var changes = words.Length == 0
                    ? Array.Empty<TextChange>()
                    : [new TextChange(inA ? TextChangeKind.Removed : TextChangeKind.Added, string.Join(' ', words))];
                pages.Add(new PageComparison(i, inA, inB, 1, changes));
            }

            progress?.Report((i + 1.0) / count);
        }

        return new ComparisonResult(a.PageCount, b.PageCount, pages);
    }

    /// <summary>
    /// Renders an overlay of one page: unchanged content in gray, content only in the first document in red,
    /// content only in the second in green.
    /// </summary>
    public static unsafe SKBitmap RenderDifference(byte[] first, byte[] second, int pageIndex, double scale = 1.5)
    {
        using var a = new PdfView(first);
        using var b = new PdfView(second);
        using var bitmapA = pageIndex < a.PageCount ? a.Render(pageIndex, scale) : null;
        using var bitmapB = pageIndex < b.PageCount ? b.Render(pageIndex, scale) : null;
        if (bitmapA == null && bitmapB == null) throw new ArgumentOutOfRangeException(nameof(pageIndex));

        int w = Math.Max(bitmapA?.Width ?? 0, bitmapB?.Width ?? 0), h = Math.Max(bitmapA?.Height ?? 0, bitmapB?.Height ?? 0);
        var result = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var dst = (uint*)result.GetPixels();
        var stride = result.RowBytes / 4;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var pa = Pixel(bitmapA, x, y);
            var pb = Pixel(bitmapB, x, y);
            int la = ImageAnalysis.LumaOf(pa), lb = ImageAnalysis.LumaOf(pb);
            uint color;
            if (!ImageAnalysis.Differs(pa, pb, 32))
            {
                var faded = (uint)(170 + la / 3);
                color = 0xFF000000 | (faded << 16) | (faded << 8) | faded;
            }
            else if (la < lb)
            {
                color = 0xFFD92D20; // darker in the first document: removed
            }
            else
            {
                color = 0xFF12A150; // darker in the second document: added
            }

            dst[y * stride + x] = color;
        }

        return result;
    }

    private static unsafe uint Pixel(SKBitmap? bitmap, int x, int y)
    {
        if (bitmap == null || x >= bitmap.Width || y >= bitmap.Height) return 0xFFFFFFFF;
        return ((uint*)bitmap.GetPixels())[y * (bitmap.RowBytes / 4) + x];
    }

    private static string[] SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Word-level diff (longest common subsequence). Consecutive changes of the same kind are merged.</summary>
    internal static IReadOnlyList<TextChange> DiffWords(string first, string second)
    {
        var a = SplitWords(first);
        var b = SplitWords(second);

        // Trim the common prefix and suffix so the quadratic table only covers the part that changed.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var midA = a.AsSpan(prefix, a.Length - prefix - suffix);
        var midB = b.AsSpan(prefix, b.Length - prefix - suffix);
        if (midA.Length == 0 && midB.Length == 0) return [];

        var changes = new List<TextChange>();
        if ((long)midA.Length * midB.Length > 16_000_000)
        {
            // Too large for a table: report the differing blocks wholesale.
            if (midA.Length > 0) changes.Add(new TextChange(TextChangeKind.Removed, string.Join(' ', midA.ToArray())));
            if (midB.Length > 0) changes.Add(new TextChange(TextChangeKind.Added, string.Join(' ', midB.ToArray())));
            return changes;
        }

        int n = midA.Length, m = midB.Length;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            table[i, j] = midA[i] == midB[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

        var removed = new List<string>();
        var added = new List<string>();

        void Flush()
        {
            if (removed.Count > 0) changes.Add(new TextChange(TextChangeKind.Removed, string.Join(' ', removed)));
            if (added.Count > 0) changes.Add(new TextChange(TextChangeKind.Added, string.Join(' ', added)));
            removed.Clear();
            added.Clear();
        }

        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && midA[x] == midB[y])
            {
                Flush();
                x++;
                y++;
            }
            else if (y < m && (x == n || table[x, y + 1] >= table[x + 1, y]))
            {
                added.Add(midB[y++]);
            }
            else
            {
                removed.Add(midA[x++]);
            }
        }

        Flush();
        return changes;
    }
}
