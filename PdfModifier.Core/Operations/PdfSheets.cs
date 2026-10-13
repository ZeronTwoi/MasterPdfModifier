using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Pdfium;
using PdfModifier.Core.Text;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

/// <summary>How the pages found on one sheet are put in order.</summary>
public enum SheetOrder
{
    /// <summary>By the page numbers printed on them; pages without one stay where reading order puts them.</summary>
    Automatic,

    /// <summary>Row by row from the top, each row from left to right.</summary>
    Rows,

    /// <summary>Row by row from the top, each row from right to left.</summary>
    RowsRightToLeft,

    /// <summary>Column by column from the left, each column from top to bottom.</summary>
    Columns
}

/// <summary>
/// What was found on a sheet: the pages on it (in visual coordinates and reading order), how much speaks for that
/// result (0-1), and whether it is a close call that someone should look at before the sheet is cut.
/// </summary>
public sealed record SheetFinding(IReadOnlyList<RectD> Pages, double Certainty, bool Doubtful)
{
    /// <summary>
    /// For each page, whether where it ends is a close call: something that is not paper hangs on to its edge and
    /// could not be cut away within what the setting allows. Empty when no page is in doubt.
    /// </summary>
    public IReadOnlyList<bool> EdgesInDoubt { get; init; } = [];

    public bool EdgeInDoubt(int page) => page < EdgesInDoubt.Count && EdgesInDoubt[page];

    public bool AnyEdgeInDoubt => EdgesInDoubt.Contains(true);
}

/// <summary>
/// What is done about the edges of the pages found on a sheet. A page as it is found often comes with things that
/// are not page: the dark of the scanner around a book, the shadow in its fold, a sliver of the page next to it.
/// These are left in (<see cref="AsFound"/>), looked for and cut away (<see cref="Automatic"/>), or taken off as
/// strips of a set width (<see cref="ByHand"/>).
/// </summary>
public sealed record SheetEdges
{
    /// <summary>The pages are taken as they are found.</summary>
    public static SheetEdges AsFound { get; } = new();

    /// <summary>
    /// How close to the page the cut goes (0-1). At 0 nothing is cut away. Up to about 0.7 the cut moves in further
    /// and further to leave out what is not paper; beyond that it goes on into the page's own margins, and at 1 it
    /// lies right around what is printed.
    /// </summary>
    public double Closeness { get; init; }

    /// <summary>Strips taken off at the edges of the sheet, as shares of its width (left, right) and height (top, bottom).</summary>
    public double Left { get; init; }

    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }

    /// <summary>The strip taken off each page where it meets another page of the sheet, as a share of the sheet.</summary>
    public double Between { get; init; }

    public bool TakesStrips => Left > 0 || Top > 0 || Right > 0 || Bottom > 0 || Between > 0;

    public static SheetEdges Automatic(double closeness) => new() { Closeness = Math.Clamp(closeness, 0, 1) };

    public static SheetEdges ByHand(double left, double top, double right, double bottom, double between) => new()
    {
        Left = Math.Clamp(left, 0, 0.45), Top = Math.Clamp(top, 0, 0.45), Right = Math.Clamp(right, 0, 0.45),
        Bottom = Math.Clamp(bottom, 0, 0.45), Between = Math.Clamp(between, 0, 0.45)
    };
}

/// <summary>
/// Sheets that show several pages at once: a scan of an open book, a photograph of pages lying next to each other, a
/// print with two or four pages per sheet. Finds the pages, reads the numbers printed on them and cuts the sheet into
/// pages of their own.
/// </summary>
public static unsafe partial class PdfSheets
{
    /// <summary>Longer side, in pixels, of the picture the pages are looked for in.</summary>
    private const int WorkSize = 900;

    /// <summary>More pages than this next to or above each other are not looked for.</summary>
    private const int MostAcross = 4;

    // ---- finding the pages --------------------------------------------------------------------------------------

    /// <summary>The sensitivity at which only divisions that are well supported are made.</summary>
    public const double UsualSensitivity = 0.5;

    /// <summary>
    /// Looks for the separate pages shown on a page. <paramref name="sensitivity"/> (0-1) says how readily a sheet
    /// is divided: at 0 only where everything speaks for it, at 1 wherever an empty band lies where pages would
    /// meet. A sheet that shows just one page comes back as a single area: the whole page, or the sheet of paper on
    /// it when that lies on a background.
    /// </summary>
    /// <param name="edges">What is done about the edges of the pages found; null takes them as they are found.</param>
    public static SheetFinding Find(PdfView view, int pageIndex, double sensitivity = UsualSensitivity, SheetEdges? edges = null) =>
        OnPage(view, pageIndex, raster => Look(raster, Limit(sensitivity), edges ?? SheetEdges.AsFound));

    /// <summary>As <see cref="Find(PdfView,int,double,SheetEdges)"/> for a picture; the areas are in pixels.</summary>
    public static SheetFinding Find(SKBitmap image, double sensitivity = UsualSensitivity, SheetEdges? edges = null) =>
        OnImage(image, raster => Look(raster, Limit(sensitivity), edges ?? SheetEdges.AsFound));

    /// <summary>The pages <see cref="Find(PdfView,int,double,SheetEdges)"/> finds at the usual sensitivity.</summary>
    public static IReadOnlyList<RectD> Detect(PdfView view, int pageIndex) => Find(view, pageIndex).Pages;

    public static IReadOnlyList<RectD> Detect(SKBitmap image) => Find(image).Pages;

    /// <summary>
    /// Divides a page into <paramref name="columns"/> × <paramref name="rows"/> equal parts. A cut that falls close
    /// to an empty band or a fold moves into it, so the parts end where the pages do.
    /// </summary>
    public static IReadOnlyList<RectD> Grid(PdfView view, int pageIndex, int columns, int rows) => Divide(view, pageIndex, columns, rows).Pages;

    public static IReadOnlyList<RectD> Grid(SKBitmap image, int columns, int rows) => Divide(image, columns, rows).Pages;

    /// <summary>As <see cref="Grid(PdfView,int,int,int)"/>, with the edges of the parts seen to as <paramref name="edges"/> says.</summary>
    public static SheetFinding Divide(PdfView view, int pageIndex, int columns, int rows, SheetEdges? edges = null) =>
        OnPage(view, pageIndex, raster => Divide(raster, columns, rows, edges ?? SheetEdges.AsFound));

    public static SheetFinding Divide(SKBitmap image, int columns, int rows, SheetEdges? edges = null) =>
        OnImage(image, raster => Divide(raster, columns, rows, edges ?? SheetEdges.AsFound));

    /// <summary>How much has to speak for a division before it is made (see <see cref="Evidence"/>).</summary>
    private static double Limit(double sensitivity) => 0.85 - 0.7 * Math.Clamp(sensitivity, 0, 1);

    /// <summary>Within this distance of the limit a result is a close call.</summary>
    private const double CloseCall = 0.12;

    /// <param name="Unsure">For each page, whether where it ends is a close call; null when none is.</param>
    private sealed record Found(List<Box> Pages, double Certainty, bool Doubtful, List<bool>? Unsure = null);

    private static SheetFinding OnPage(PdfView view, int pageIndex, Func<Raster, Found> find)
    {
        var info = view.Pages[pageIndex];
        var scale = Math.Min(3, WorkSize / Math.Max(1, Math.Max(info.Width, info.Height)));
        using var bitmap = view.Render(pageIndex, scale);
        return Finding(find(Raster.Read(bitmap)), info.Width / bitmap.Width, info.Height / bitmap.Height);
    }

    /// <summary>What was found, in the units of whoever asked and in reading order.</summary>
    private static SheetFinding Finding(Found found, double fx, double fy)
    {
        var areas = found.Pages.Select(b => new RectD(b.Left * fx, b.Top * fy, b.Right * fx, b.Bottom * fy)).ToList();
        var order = Order(areas, SheetOrder.Rows);
        return new SheetFinding(order.Select(i => areas[i]).ToList(), found.Certainty, found.Doubtful)
        {
            EdgesInDoubt = found.Unsure is { } unsure && unsure.Contains(true) ? order.Select(i => unsure[i]).ToList() : []
        };
    }

    private static SheetFinding OnImage(SKBitmap image, Func<Raster, Found> find)
    {
        var factor = Math.Min(1.0, (double)WorkSize / Math.Max(image.Width, image.Height));
        SKBitmap? converted = null, small = null;
        try
        {
            var source = image;
            if (source.ColorType != SKColorType.Bgra8888) source = converted = image.Copy(SKColorType.Bgra8888);
            if (factor < 1)
                source = small = ImageFilters.Resize(source, Math.Max(1, (int)Math.Round(image.Width * factor)),
                    Math.Max(1, (int)Math.Round(image.Height * factor)));
            return Finding(find(Raster.Read(source)), (double)image.Width / source.Width, (double)image.Height / source.Height);
        }
        finally
        {
            small?.Dispose();
            converted?.Dispose();
        }
    }

    private readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    /// <summary>The picture reduced to what the search needs: how light each pixel is and how far from the background.</summary>
    private sealed class Raster
    {
        public int Width { get; private init; }
        public int Height { get; private init; }
        public byte[] Luma { get; private init; } = [];

        /// <summary>Distance of each pixel's colour from the colour around the edge of the picture.</summary>
        public byte[] Away { get; private init; } = [];

        /// <summary>Pixels that stand out from the background, at the threshold the pages were found with.</summary>
        public bool[] Different { get; set; } = [];

        /// <summary>Background that can be reached from the edge of the picture: what lies around the pages.</summary>
        public bool[] Outside { get; set; } = [];

        public static Raster Read(SKBitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            var pixels = (uint*)bitmap.GetPixels();
            var stride = bitmap.RowBytes / 4;
            var background = ImageAnalysis.EstimateBorderColor(pixels, w, h, stride);
            int bgB = (int)(background & 0xFF), bgG = (int)((background >> 8) & 0xFF), bgR = (int)((background >> 16) & 0xFF);
            var luma = new byte[w * h];
            var away = new byte[w * h];
            for (var y = 0; y < h; y++)
            {
                var row = pixels + y * stride;
                for (var x = 0; x < w; x++)
                {
                    var p = row[x];
                    int b = (int)(p & 0xFF), g = (int)((p >> 8) & 0xFF), r = (int)((p >> 16) & 0xFF);
                    luma[y * w + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    away[y * w + x] = (byte)Math.Max(Math.Abs(b - bgB), Math.Max(Math.Abs(g - bgG), Math.Abs(r - bgR)));
                }
            }

            var raster = new Raster { Width = w, Height = h, Luma = luma, Away = away };
            raster.Different = new bool[w * h];
            raster.Outside = new bool[w * h];
            return raster;
        }

        public void Threshold(int level)
        {
            for (var i = 0; i < Away.Length; i++) Different[i] = Away[i] > level;
            FloodOutside();
        }

        private void FloodOutside()
        {
            int w = Width, h = Height;
            Array.Clear(Outside);
            var stack = new Stack<int>();

            void Visit(int index)
            {
                if (Different[index] || Outside[index]) return;
                Outside[index] = true;
                stack.Push(index);
            }

            for (var x = 0; x < w; x++)
            {
                Visit(x);
                Visit((h - 1) * w + x);
            }

            for (var y = 0; y < h; y++)
            {
                Visit(y * w);
                Visit(y * w + w - 1);
            }

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                int x = index % w, y = index / w;
                if (x > 0) Visit(index - 1);
                if (x < w - 1) Visit(index + 1);
                if (y > 0) Visit(index - w);
                if (y < h - 1) Visit(index + w);
            }
        }
    }

    private static Found Look(Raster raster, double limit, SheetEdges edges)
    {
        var (regions, clear, apart) = Regions(raster);
        var pages = new List<Box>();
        var certainty = clear;
        var doubtful = clear < 0.7;
        // Sheets lying apart are pages already; only what holds them together (the dark line where two of them touch)
        // divides them further. A single sheet may also be divided along its empty bands.
        foreach (var region in regions)
        {
            var cut = Cut(raster, region, linesOnly: regions.Count > 1, limit);
            pages.AddRange(cut.Pages);
            certainty = Math.Min(certainty, cut.Certainty);
            doubtful |= cut.Doubtful;
        }

        return Edged(raster, new Found(pages, certainty, doubtful), apart, edges);
    }

    private static Found Divide(Raster raster, int columns, int rows, SheetEdges edges)
    {
        columns = Math.Clamp(columns, 1, 12);
        rows = Math.Clamp(rows, 1, 12);
        var (regions, _, apart) = Regions(raster);
        var region = regions.Count == 1 ? regions[0] : new Box(0, 0, raster.Width, raster.Height);
        var profile = new Profile(raster, region);
        // Divided the way someone asked for: there is nothing to doubt about that.
        var found = new Found(Cells(region, Snapped(profile.Columns, region.Left, region.Width, columns, profile.Sides),
            Snapped(profile.Rows, region.Top, region.Height, rows, profile.Ends)), 1, false);
        return Edged(raster, found, apart, edges);
    }

    /// <summary>
    /// The sheets of paper in the picture: areas that stand out from the background as a whole, and how clearly they
    /// do (0-1). A picture that is one sheet from edge to edge (the usual scan) is a single region covering all of it,
    /// and <c>Apart</c> is false: nothing was told apart from a background then.
    /// </summary>
    private static (List<Box> Sheets, double Clear, bool Apart) Regions(Raster raster)
    {
        int w = raster.Width, h = raster.Height;
        var histogram = new long[256];
        foreach (var value in raster.Away) histogram[value]++;
        var level = Math.Clamp(ImageFilters.OtsuThreshold(histogram), 12, 80);

        // Paper on a dark table differs a lot from it; paper on a light grey one (a screenshot of a viewer) only a
        // little, which the first threshold may take for the same thing.
        foreach (var attempt in level > 14 ? new[] { level, 12 } : [level])
        {
            raster.Threshold(attempt);
            if (Sheets(raster) is { Sheets.Count: > 0 } found) return (found.Sheets, found.Clear, true);
        }

        raster.Threshold(level);
        return ([new Box(0, 0, w, h)], 1, false);
    }

    private static (List<Box> Sheets, double Clear)? Sheets(Raster raster)
    {
        int w = raster.Width, h = raster.Height;
        var different = raster.Different;
        var total = different.Count(d => d);
        if (total < w * h * 0.02) return null;

        // Closing the gaps first keeps a sheet in one piece where writing as dark as the background crosses it;
        // shrinking it afterwards cuts the thin bridges (a shadow, a corner) between sheets lying close together.
        var close = Math.Max(2, (int)Math.Round(Math.Min(w, h) * 0.008));
        var open = Math.Max(2, (int)Math.Round(Math.Min(w, h) * 0.006));
        var solid = Spread(Spread(different, w, h, close, true), w, h, close + open, false);

        var sheets = new List<Box>();
        var thinnest = 1.0;
        foreach (var found in Components(solid, w, h))
        {
            var box = new Box(Math.Max(0, found.Left - open), Math.Max(0, found.Top - open), Math.Min(w, found.Right + open), Math.Min(h, found.Bottom + open));
            if (box.Width < w * 0.1 || box.Height < h * 0.1) continue;
            // A sheet of paper differs from the background all over; a block of text only where its letters are.
            var filled = 0;
            for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++)
                if (different[y * w + x])
                    filled++;
            var share = (double)filled / (box.Width * box.Height);
            if (share < 0.6) continue;
            sheets.Add(box);
            thinnest = Math.Min(thinnest, share);
        }

        if (sheets.Count == 0) return null;

        // What stands out has to be the sheets and little else: two photographs on a page of text are not two pages.
        var elsewhere = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (different[y * w + x] && !sheets.Any(s => s.Contains(x, y)))
                elsewhere++;
        var stray = (double)elsewhere / total;
        if (stray > 0.12) return null;
        // Paper that only just counts as one piece, or much that lies around it, makes the finding less sure.
        var clear = Math.Min(0.5 + 0.5 * Ramp(thinnest, 0.6, 0.85), 1 - 0.5 * Ramp(stray, 0.03, 0.12));
        return (sheets, clear);
    }

    private static double Ramp(double value, double from, double to) => Math.Clamp((value - from) / (to - from), 0, 1);

    /// <summary>Sets every pixel that has one equal to <paramref name="value"/> within <paramref name="radius"/> to that value.</summary>
    private static bool[] Spread(bool[] mask, int w, int h, int radius, bool value)
    {
        var across = new bool[mask.Length];
        var sums = new int[Math.Max(w, h) + 1];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) sums[x + 1] = sums[x] + (mask[y * w + x] == value ? 1 : 0);
            for (var x = 0; x < w; x++)
                across[y * w + x] = sums[Math.Min(w, x + radius + 1)] - sums[Math.Max(0, x - radius)] > 0 ? value : !value;
        }

        var result = new bool[mask.Length];
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++) sums[y + 1] = sums[y] + (across[y * w + x] == value ? 1 : 0);
            for (var y = 0; y < h; y++)
                result[y * w + x] = sums[Math.Min(h, y + radius + 1)] - sums[Math.Max(0, y - radius)] > 0 ? value : !value;
        }

        return result;
    }

    /// <summary>The bounding boxes of the connected areas of a mask.</summary>
    private static List<Box> Components(bool[] mask, int w, int h)
    {
        var seen = new bool[mask.Length];
        var boxes = new List<Box>();
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            int left = w, top = h, right = 0, bottom = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                int x = index % w, y = index / w;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);

                void Visit(int next)
                {
                    if (!mask[next] || seen[next]) return;
                    seen[next] = true;
                    stack.Push(next);
                }

                if (x > 0) Visit(index - 1);
                if (x < w - 1) Visit(index + 1);
                if (y > 0) Visit(index - w);
                if (y < h - 1) Visit(index + w);
            }

            boxes.Add(new Box(left, top, right, bottom));
        }

        return boxes;
    }

    /// <summary>An empty band across a sheet, or the line of a fold. Positions run along one axis of the picture.</summary>
    private sealed record Gap(int Start, int End, double? Line)
    {
        public int Width => End - Start;
        public double Centre => Line ?? (Start + End) / 2.0;
    }

    /// <summary>What is printed on one sheet, counted along its columns and its rows.</summary>
    private sealed class Profile
    {
        private readonly bool[] _ink;
        private readonly Box _region;

        public Profile(Raster raster, Box region)
        {
            _region = region;
            int w = region.Width, h = region.Height;
            var levels = new int[256];
            for (var y = region.Top; y < region.Bottom; y++)
            for (var x = region.Left; x < region.Right; x++)
                levels[raster.Luma[y * raster.Width + x]]++;
            // The paper is whatever shade most of the sheet has.
            var paper = 0;
            long most = -1;
            for (var level = 2; level < 254; level++)
            {
                long around = levels[level - 2] + levels[level - 1] + levels[level] + levels[level + 1] + levels[level + 2];
                if (around > most) (most, paper) = (around, level);
            }

            _ink = new bool[w * h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var index = (y + region.Top) * raster.Width + x + region.Left;
                // The table showing around a sheet that lies askew is not something printed on it.
                _ink[y * w + x] = !raster.Outside[index] && Math.Abs(raster.Luma[index] - paper) > 55;
            }

            var columns = new int[w];
            var rows = new int[h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (_ink[y * w + x])
                {
                    columns[x]++;
                    rows[y]++;
                }

            // A line from one side of the sheet to the other (a fold, a frame, the edge of the scanner glass) says
            // nothing about what is printed where; it is left out when the other direction is counted.
            var lineColumns = columns.Select(count => count >= h * 0.6).ToArray();
            var lineRows = rows.Select(count => count >= w * 0.6).ToArray();
            Array.Clear(columns);
            Array.Clear(rows);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!_ink[y * w + x]) continue;
                if (!lineRows[y]) columns[x]++;
                if (!lineColumns[x]) rows[y]++;
            }

            (Columns, Sides) = Bands(columns, lineColumns, lineRows.Count(line => !line), region.Left);
            (Rows, Ends) = Bands(rows, lineRows, lineColumns.Count(line => !line), region.Top);
        }

        /// <summary>Empty bands running down the sheet (between pages side by side), by their place along x.</summary>
        public List<Gap> Columns { get; }

        /// <summary>Empty bands running across the sheet (between pages above each other), by their place along y.</summary>
        public List<Gap> Rows { get; }

        /// <summary>The empty margins left and right of everything printed.</summary>
        public (int Leading, int Trailing) Sides { get; }

        /// <summary>The empty margins above and below everything printed.</summary>
        public (int Leading, int Trailing) Ends { get; }

        private static (List<Gap> Gaps, (int Leading, int Trailing) Margins) Bands(int[] counts, bool[] lines, int length, int origin)
        {
            // A few specks of dust do not make a band less empty.
            var tolerance = Math.Max(1, (int)(length * 0.004));
            var bands = new List<(int Start, int End, double LineSum, int LineCount)>();
            for (var start = 0; start < counts.Length;)
            {
                if (!lines[start] && counts[start] > tolerance)
                {
                    start++;
                    continue;
                }

                var end = start;
                double lineSum = 0;
                var lineCount = 0;
                while (end < counts.Length && (lines[end] || counts[end] <= tolerance))
                {
                    if (lines[end])
                    {
                        lineSum += end + 0.5;
                        lineCount++;
                    }

                    end++;
                }

                bands.Add((start, end, lineSum, lineCount));
                start = end;
            }

            // The shadow in a fold has shoulders, and a book that lies a little askew smears its line over several
            // columns: next to a line, a few columns that are neither empty nor line belong to it and to the room
            // around it.
            var skirt = Math.Max(2, (int)Math.Round(counts.Length * 0.012));
            for (var i = 0; i + 1 < bands.Count;)
            {
                var (first, second) = (bands[i], bands[i + 1]);
                if (second.Start - first.End > skirt || first.LineCount + second.LineCount == 0)
                {
                    i++;
                    continue;
                }

                bands[i] = (first.Start, second.End, first.LineSum + second.LineSum, first.LineCount + second.LineCount);
                bands.RemoveAt(i + 1);
            }

            var gaps = new List<Gap>();
            int leading = 0, trailing = 0;
            foreach (var (start, end, lineSum, lineCount) in bands)
                if (start == 0) leading = end;
                else if (end == counts.Length) trailing = end - start;
                else gaps.Add(new Gap(start + origin, end + origin, lineCount > 0 ? lineSum / lineCount + origin : null));
            return (gaps, (leading, trailing));
        }

        /// <summary>
        /// The box around everything printed in <paramref name="cell"/> when that is a frame or a filled rectangle
        /// (a slide, a page drawn with its border); null otherwise.
        /// </summary>
        public Box? Frame(Box cell)
        {
            int w = _region.Width, left = cell.Right, top = cell.Bottom, right = cell.Left, bottom = cell.Top;
            for (var y = cell.Top; y < cell.Bottom; y++)
            for (var x = cell.Left; x < cell.Right; x++)
            {
                if (!_ink[(y - _region.Top) * w + x - _region.Left]) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);
            }

            if (right - left < cell.Width * 0.5 || bottom - top < cell.Height * 0.5) return null;

            bool Drawn(int x, int y) => _ink[(y - _region.Top) * w + x - _region.Left];

            // Every edge of the box has ink along nearly all of its length.
            const int depth = 3;
            int along = 0, across = 0;
            for (var x = left; x < right; x++)
            {
                var upper = false;
                var lower = false;
                for (var d = 0; d < depth && top + d < bottom; d++)
                {
                    upper |= Drawn(x, top + d);
                    lower |= Drawn(x, bottom - 1 - d);
                }

                if (upper && lower) along++;
            }

            for (var y = top; y < bottom; y++)
            {
                var near = false;
                var far = false;
                for (var d = 0; d < depth && left + d < right; d++)
                {
                    near |= Drawn(left + d, y);
                    far |= Drawn(right - 1 - d, y);
                }

                if (near && far) across++;
            }

            return along >= (right - left) * 0.85 && across >= (bottom - top) * 0.85 ? new Box(left, top, right, bottom) : null;
        }
    }

    /// <summary>One way of dividing a sheet evenly and how much speaks for it (0-1).</summary>
    private sealed record Division(int[] Across, int[] Down, int Parts, double Score);

    /// <summary>
    /// Divides a sheet into the pages on it, or returns it whole when not enough speaks for any even division.
    /// <paramref name="limit"/> is how much has to speak for one.
    /// </summary>
    private static Found Cut(Raster raster, Box region, bool linesOnly, double limit)
    {
        var profile = new Profile(raster, region);
        var divisions = new List<Division>();
        for (var columns = 1; columns <= MostAcross; columns++)
        for (var rows = 1; rows <= MostAcross; rows++)
        {
            if (columns * rows < 2) continue;
            var shape = Shape((double)region.Width / columns, (double)region.Height / rows);
            if (shape == 0) continue;
            if (Cuts(profile.Columns, region.Left, region.Width, columns, profile.Sides, linesOnly) is not { } across) continue;
            if (Cuts(profile.Rows, region.Top, region.Height, rows, profile.Ends, linesOnly) is not { } down) continue;
            var score = Math.Min(across.Score, down.Score);
            // Pages that would lie on their side are the exception, unless each is plainly a thing of its own:
            // a slide with its frame or its coloured ground.
            if (shape < 1 && !Cells(region, across.At, down.At).All(cell => profile.Frame(cell) != null)) score *= shape;
            divisions.Add(new Division(across.At, down.At, columns * rows, score));
        }

        var chosen = divisions.Where(d => d.Score >= limit).OrderByDescending(d => d.Parts).ThenByDescending(d => d.Score).FirstOrDefault();
        // What nearly was: a division into more pages than the one made that missed the limit by little.
        var nearly = divisions.Where(d => d.Parts > (chosen?.Parts ?? 1)).Select(d => d.Score).DefaultIfEmpty(0).Max();
        var missed = nearly >= Math.Max(0.2, limit - CloseCall);
        if (chosen == null) return new Found([region], 1 - nearly, missed);
        return new Found(Cells(region, chosen.Across, chosen.Down).Select(cell => profile.Frame(cell) ?? cell).ToList(),
            chosen.Score, missed || chosen.Score < limit + CloseCall);
    }

    /// <summary>
    /// How likely parts of this size are pages, going by their shape alone: 1 for upright pages, less for pages
    /// lying on their side, 0 for strips that no page is.
    /// </summary>
    private static double Shape(double width, double height)
    {
        var tall = height / width;
        return tall switch
        {
            > 2.4 or < 1 / 2.4 => 0,
            >= 1.15 and <= 1.8 => 1,
            > 0.87 and < 1.15 => 0.75,
            >= 0.55 and <= 0.87 => 0.6,
            _ => 0.5
        };
    }

    /// <summary>Where a sheet can be cut along one direction, and how much speaks for the weakest of the cuts.</summary>
    private sealed record Cutting(int[] At, double Score);

    /// <summary>
    /// Where a sheet of <paramref name="length"/> can be cut into <paramref name="parts"/> about equal ones: an empty
    /// band or a fold has to lie near each of the places. Null when one of them has neither.
    /// </summary>
    private static Cutting? Cuts(List<Gap> gaps, int origin, int length, int parts, (int Leading, int Trailing) margins, bool linesOnly)
    {
        if (parts == 1) return new Cutting([], 1);
        var step = (double)length / parts;
        var chosen = new List<Gap>();
        for (var i = 1; i < parts; i++)
        {
            if (Between(gaps, origin + i * step, step * 0.15, length, length * 0.015, linesOnly) is not { } gap) return null;
            chosen.Add(gap);
        }

        var at = new int[parts - 1];
        var score = 1.0;
        for (var i = 1; i < parts; i++)
        {
            var target = origin + i * step;
            at[i - 1] = (int)Math.Round(Place(chosen[i - 1], target, step));
            score = Math.Min(score, Evidence(chosen[i - 1], gaps, chosen, target, step, length, margins));
        }

        return new Cutting(at, score);
    }

    /// <summary>
    /// How much speaks for a band being the space between two pages rather than a gap within one (0-1).
    /// <para>
    /// The space between two pages is made of the margins of both. So it is wide for the page (a paragraph gap is
    /// not), it stands out among the other gaps on the sheet (one paragraph gap of many does not), and it is about as
    /// wide as the margins around the sheet or wider (the gap between two columns of text is much narrower). It also
    /// lies where the sheet divides evenly. A dark line with room on both sides, the fold of a book or the table
    /// showing between two sheets, settles the matter on its own.
    /// </para>
    /// </summary>
    private static double Evidence(Gap gap, List<Gap> gaps, List<Gap> taken, double target, double step, int length,
        (int Leading, int Trailing) margins)
    {
        var line = gap.Line != null && gap.Width >= length * 0.02 ? 0.9 : 0;
        var wide = Ramp(gap.Width / step, 0.04, 0.16);
        var widestOther = gaps.Where(g => g.Line == null && !taken.Contains(g)).Select(g => g.Width).DefaultIfEmpty(0).Max();
        var standsOut = widestOther == 0 ? 1 : Ramp((double)gap.Width / widestOther, 1.0, 2.5);
        // A sheet cropped to its content has no margin to compare with, which says neither yes nor no.
        var least = length * 0.015;
        var around = (margins.Leading >= least, margins.Trailing >= least) switch
        {
            (true, true) => (margins.Leading + margins.Trailing) / 2.0,
            (true, false) => margins.Leading,
            (false, true) => margins.Trailing,
            _ => 0
        };
        var likeMargins = around == 0 ? 0.6 : Ramp(gap.Width / around, 0.5, 1.1);
        var inPlace = 1 - 0.5 * Ramp(Off(gap, target) / step, 0.03, 0.15);
        return Math.Max(line, inPlace * (0.35 * wide + 0.35 * standsOut + 0.3 * likeMargins));
    }

    /// <summary>How far a band lies from the place where the sheet divides evenly; nothing when it reaches across it.</summary>
    private static double Off(Gap gap, double target) =>
        gap.Line is { } line ? Math.Abs(line - target)
        : target >= gap.Start && target <= gap.End ? 0
        : Math.Abs(gap.Centre - target);

    /// <summary>Where in a band the cut is made.</summary>
    private static double Place(Gap gap, double target, double step)
    {
        // The space between two pages is cut down its middle, which gives each page its own margin.
        if (gap.Line != null || target < gap.Start || target > gap.End || gap.Width <= step * 0.3) return gap.Centre;
        // A band much wider than two margins is that space together with the empty rest of one of the pages: the
        // cut belongs where the sheet divides evenly, not in the middle of all that.
        var inset = Math.Min(gap.Width / 4.0, step * 0.02);
        return Math.Clamp(target, gap.Start + inset, gap.End - inset);
    }

    /// <summary>Equal divisions, each moved into the band between two pages or onto a fold when one is close by.</summary>
    private static int[] Snapped(List<Gap> gaps, int origin, int length, int parts, (int Leading, int Trailing) margins)
    {
        var cuts = new int[parts - 1];
        var step = (double)length / parts;
        var least = Math.Max(length * 0.025, Math.Min(margins.Leading, margins.Trailing) * 0.6);
        for (var i = 1; i < parts; i++)
        {
            var target = origin + i * step;
            cuts[i - 1] = (int)Math.Round(Between(gaps, target, step * 0.2, length, least, false) is { } gap ? Place(gap, target, step) : target);
        }

        return cuts;
    }

    /// <summary>
    /// The band or fold that may separate two pages near <paramref name="target"/>. Of several bands the one nearest
    /// to it counts, unless it is much narrower than another: the space between the text and the page number under
    /// it can be as wide as the space between two pages, but lies off the place where the sheet divides evenly.
    /// </summary>
    private static Gap? Between(List<Gap> gaps, double target, double reach, int length, double least, bool linesOnly)
    {
        var fitting = gaps
            .Where(g => Off(g, target) <= reach && (g.Line != null ? g.Width >= length * 0.02 : !linesOnly && g.Width >= least))
            .ToList();
        if (fitting.Count == 0) return null;
        var widest = fitting.Max(g => g.Width);
        return fitting.Where(g => g.Width >= widest * 0.6).MinBy(g => Off(g, target));
    }

    private static List<Box> Cells(Box region, int[] across, int[] down)
    {
        int[] xs = [region.Left, ..across, region.Right], ys = [region.Top, ..down, region.Bottom];
        var cells = new List<Box>();
        for (var row = 0; row + 1 < ys.Length; row++)
        for (var column = 0; column + 1 < xs.Length; column++)
            if (xs[column + 1] - xs[column] >= 2 && ys[row + 1] - ys[row] >= 2)
                cells.Add(new Box(xs[column], ys[row], xs[column + 1], ys[row + 1]));
        return cells;
    }

    // ---- where a page ends --------------------------------------------------------------------------------------

    /// <summary>
    /// Up to this closeness the cut only moves in to leave out what is not page; beyond it, it goes on into the
    /// page's own margins.
    /// </summary>
    private const double ToThePage = 0.7;

    /// <summary>A line of an area counts as band (the dark around a book, the table) when this much of it is.</summary>
    private const double BandShare = 0.85;

    /// <summary>No more than this share of an area is given up at one side, whatever is found there.</summary>
    private const double MostCutOff = 0.45;

    private static double Mix(double from, double to, double share) => from + (to - from) * Math.Clamp(share, 0, 1);

    /// <summary>Sees to the edges of the pages found the way <paramref name="edges"/> says.</summary>
    private static Found Edged(Raster raster, Found found, bool apart, SheetEdges edges)
    {
        if (edges.Closeness <= 0 && !edges.TakesStrips) return found;
        var pages = new List<Box>();
        var unsure = new List<bool>();
        foreach (var page in found.Pages)
        {
            var (area, open) = edges.Closeness > 0 ? Trimmed(raster, page, apart, edges.Closeness) : (page, false);
            pages.Add(area);
            unsure.Add(open);
        }

        if (edges.TakesStrips) pages = Stripped(pages, found.Pages, raster.Width, raster.Height, edges);
        return found with { Pages = pages, Unsure = unsure };
    }

    /// <summary>
    /// The shade of the paper across an area. Where the light fell unevenly it gets lighter or darker from one side
    /// to the other, and what is paper has to be judged against the shade the paper has at that spot.
    /// </summary>
    private readonly record struct Paper(double Middle, double Across, double Down, double CenterX, double CenterY)
    {
        public double At(int x, int y) => Middle + Across * (x - CenterX) + Down * (y - CenterY);
    }

    private static Paper PaperOf(Raster raster, Box box)
    {
        const int cells = 6;
        // The middle of the area is page, whatever lies around its edges.
        double left = box.Left + box.Width * 0.15, top = box.Top + box.Height * 0.15;
        double stepX = box.Width * 0.7 / cells, stepY = box.Height * 0.7 / cells;
        var samples = new List<(double X, double Y, double Shade)>();
        var levels = new int[256];
        for (var row = 0; row < cells; row++)
        for (var column = 0; column < cells; column++)
        {
            Array.Clear(levels);
            int x0 = (int)(left + column * stepX), x1 = Math.Max(x0 + 1, (int)(left + (column + 1) * stepX));
            int y0 = (int)(top + row * stepY), y1 = Math.Max(y0 + 1, (int)(top + (row + 1) * stepY));
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                levels[raster.Luma[y * raster.Width + x]]++;
            // Paper is the lighter part of a cell: print covers less of it than it leaves free.
            long count = (long)(x1 - x0) * (y1 - y0), below = 0;
            var shade = 255;
            for (var level = 0; level < 256; level++)
            {
                below += levels[level];
                if (below * 10 < count * 7) continue;
                shade = level;
                break;
            }

            samples.Add(((x0 + x1) / 2.0 - box.Left, (y0 + y1) / 2.0 - box.Top, shade));
        }

        var shades = samples.Select(s => s.Shade).Order().ToList();
        var usual = shades[shades.Count / 2];
        double cx = box.Width / 2.0, cy = box.Height / 2.0;
        // Cells that show something else (a picture, a dark heading) say nothing about the paper.
        var paper = samples.Where(s => Math.Abs(s.Shade - usual) <= 36).ToList();
        if (paper.Count < cells * cells / 3) return new Paper(usual, 0, 0, cx, cy);

        // The plane through them that fits best.
        double n = paper.Count, sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
        foreach (var (x, y, z) in paper)
        {
            double dx = x - cx, dy = y - cy;
            sx += dx;
            sy += dy;
            sz += z;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
            sxz += dx * z;
            syz += dy * z;
        }

        var det = n * (sxx * syy - sxy * sxy) - sx * (sx * syy - sxy * sy) + sy * (sx * sxy - sxx * sy);
        if (Math.Abs(det) < 1e-6) return new Paper(usual, 0, 0, cx, cy);
        var middle = (sz * (sxx * syy - sxy * sxy) - sx * (sxz * syy - sxy * syz) + sy * (sxz * sxy - sxx * syz)) / det;
        var across = (n * (sxz * syy - sxy * syz) - sz * (sx * syy - sxy * sy) + sy * (sx * syz - sxz * sy)) / det;
        var down = (n * (sxx * syz - sxz * sxy) - sx * (sx * syz - sxz * sy) + sz * (sx * sxy - sxx * sy)) / det;
        // A slope steeper than light ever falls off is something else again.
        across = Math.Clamp(across, -80.0 / box.Width, 80.0 / box.Width);
        down = Math.Clamp(down, -80.0 / box.Height, 80.0 / box.Height);
        return new Paper(middle, across, down, cx, cy);
    }

    /// <summary>What a pixel of an area is, as far as finding the page in it goes.</summary>
    [Flags]
    private enum Spot : byte
    {
        Paper = 0,

        /// <summary>Not paper: print, a shadow, whatever lies around the page.</summary>
        Marked = 1,

        /// <summary>Connected to the edge of the area through what is not paper.</summary>
        Held = 2,

        /// <summary>Held, and far from being paper: the dark around a book rather than a shadow on it.</summary>
        Far = 4,

        /// <summary>Part of the background that the sheets were told apart from.</summary>
        Outside = 8
    }

    /// <summary>
    /// The page inside the area that was found for it: the area less what hangs on to its edges without being paper.
    /// <para>
    /// Only what is connected to the edge of the area is ever taken for not being page, so print on the page, which
    /// stands free on its paper, is never cut into for looking dark. Bands that are far from paper along nearly all
    /// of a side and of one shade (the dark of the scanner around a book, the table a sheet lies on) go however wide
    /// they are. Softer things (the shadow in a fold, a sliver of the next page, the wedge a sheet lying askew leaves)
    /// go only when clean paper follows them within a distance that grows with <paramref name="closeness"/>.
    /// What could not be settled stays, and the result is reported as unsure. Past <see cref="ToThePage"/> the cut
    /// goes on into the margins.
    /// </para>
    /// </summary>
    private static (Box Area, bool Unsure) Trimmed(Raster raster, Box box, bool apart, double closeness)
    {
        int w = box.Width, h = box.Height;
        if (w < 32 || h < 32) return (box, false);
        var reach = Math.Min(1, closeness / ToThePage);
        var paper = PaperOf(raster, box);
        var tolerance = Mix(50, 24, reach);

        var spots = new Spot[w * h];
        var shades = new byte[w * h];
        var far = new bool[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var index = (y + box.Top) * raster.Width + x + box.Left;
            var shade = raster.Luma[index];
            var off = Math.Abs(shade - paper.At(x, y));
            shades[y * w + x] = shade;
            // The table around sheets that were told apart from it is no paper, however light it is.
            if (apart && raster.Outside[index]) spots[y * w + x] = Spot.Marked | Spot.Outside;
            else if (off > tolerance) spots[y * w + x] = Spot.Marked;
            far[y * w + x] = off > 64;
        }

        Hold(spots, w, h);
        for (var i = 0; i < spots.Length; i++)
            if ((spots[i] & Spot.Held) != 0 && (far[i] || (spots[i] & Spot.Outside) != 0))
                spots[i] |= Spot.Far;

        int left = 0, top = 0, right = w, bottom = h;
        Side fromLeft = default, fromTop = default, fromRight = default, fromBottom = default;
        // The sides depend on each other: a band along the top is part of every column until it has been cut off.
        for (var round = 0; round < 3; round++)
        {
            var columns = Along(spots, shades, w, h, columns: true, top, bottom, reach);
            fromLeft = Inward(columns, reach, backwards: false);
            fromRight = Inward(columns, reach, backwards: true);
            (left, right) = (fromLeft.Cut, w - fromRight.Cut);
            var rows = Along(spots, shades, w, h, columns: false, left, right, reach);
            fromTop = Inward(rows, reach, backwards: false);
            fromBottom = Inward(rows, reach, backwards: true);
            (top, bottom) = (fromTop.Cut, h - fromBottom.Cut);
        }

        // Next to nothing left: whatever this area shows, it is not a page with something around it.
        if ((long)(right - left) * (bottom - top) < 0.3 * w * h) return (box, true);

        var tighten = (closeness - ToThePage) / (1 - ToThePage);
        if (tighten > 0 && Printed(spots, w, h, left, top, right, bottom) is { } print)
        {
            // Into the margins, towards what is printed. Only on a side that ends in clean paper: where something
            // still hangs on to the edge, it may be part of the page and the print begins there.
            var room = Math.Max(2, (int)Math.Round(Math.Min(w, h) * 0.012));
            if (fromLeft.Clean) left = (int)Math.Round(Mix(left, Math.Max(left, print.Left - room), tighten));
            if (fromTop.Clean) top = (int)Math.Round(Mix(top, Math.Max(top, print.Top - room), tighten));
            if (fromRight.Clean) right = (int)Math.Round(Mix(right, Math.Min(right, print.Right + room), tighten));
            if (fromBottom.Clean) bottom = (int)Math.Round(Mix(bottom, Math.Min(bottom, print.Bottom + room), tighten));
        }

        return (new Box(box.Left + left, box.Top + top, box.Left + right, box.Top + bottom),
            fromLeft.Open || fromTop.Open || fromRight.Open || fromBottom.Open);
    }

    /// <summary>
    /// What was decided about one side of an area: how far the cut moves in, whether it ends in clean paper there,
    /// and whether something was left that could not be settled and is worth a look.
    /// </summary>
    private readonly record struct Side(int Cut, bool Clean, bool Open);

    /// <summary>Marks what is connected to the edge of an area through pixels that are not paper.</summary>
    private static void Hold(Spot[] spots, int w, int h)
    {
        var stack = new Stack<int>();

        void Visit(int index)
        {
            if ((spots[index] & Spot.Marked) == 0 || (spots[index] & Spot.Held) != 0) return;
            spots[index] |= Spot.Held;
            stack.Push(index);
        }

        for (var x = 0; x < w; x++)
        {
            Visit(x);
            Visit((h - 1) * w + x);
        }

        for (var y = 0; y < h; y++)
        {
            Visit(y * w);
            Visit(y * w + w - 1);
        }

        while (stack.Count > 0)
        {
            var index = stack.Pop();
            int x = index % w, y = index / w;
            if (x > 0) Visit(index - 1);
            if (x < w - 1) Visit(index + 1);
            if (y > 0) Visit(index - w);
            if (y < h - 1) Visit(index + w);
        }
    }

    /// <summary>
    /// An area counted along its columns or its rows. For each of those lines: how much of it hangs on to the edge
    /// (<c>Held</c>), how much of that is far from being paper (<c>Far</c>), how much of its middle stretch is anything
    /// but paper (<c>Marks</c>) and how much of that stands free on the page (<c>Print</c>), and what shade the far
    /// part has.
    /// </summary>
    /// <param name="Across">How long the lines are.</param>
    /// <param name="Within">How long their middle stretch is.</param>
    private sealed record Counts(int[] Far, int[] Held, int[] Marks, int[] Print, long[] Shade, long[] Squares, int[] Seen, int Across, int Within);

    /// <summary>
    /// Counts along the columns (or rows) of an area, each taken from <paramref name="from"/> to <paramref name="to"/>.
    /// The middle stretch leaves out as much at both ends as the sides there may still have to give up: what hangs on
    /// to the top of an area must not keep its left side from being found clean.
    /// </summary>
    private static Counts Along(Spot[] spots, byte[] shades, int w, int h, bool columns, int from, int to, double reach)
    {
        var lines = columns ? w : h;
        int[] far = new int[lines], held = new int[lines], marks = new int[lines], print = new int[lines], seen = new int[lines];
        long[] shade = new long[lines], squares = new long[lines];
        var ends = (int)Math.Ceiling((to - from) * Mix(0.008, 0.075, reach)) + 1;
        int inner0 = from + ends, inner1 = to - ends;
        int x0 = columns ? 0 : from, x1 = columns ? w : to, y0 = columns ? from : 0, y1 = columns ? to : h;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var spot = spots[y * w + x];
            if (spot == Spot.Paper) continue;
            var (line, along) = columns ? (x, y) : (y, x);
            if (along >= inner0 && along < inner1)
            {
                marks[line]++;
                if (spot == Spot.Marked) print[line]++;
            }

            if ((spot & Spot.Held) == 0) continue;
            held[line]++;
            if ((spot & Spot.Far) == 0) continue;
            far[line]++;
            // The table needs no looking at: it was told apart from the sheets as a whole.
            if ((spot & Spot.Outside) != 0) continue;
            int value = shades[y * w + x];
            shade[line] += value;
            squares[line] += value * value;
            seen[line]++;
        }

        return new Counts(far, held, marks, print, shade, squares, seen, to - from, Math.Max(0, inner1 - inner0));
    }

    /// <summary>
    /// How far the cut moves in from one side of an area, given what is found in each of the lines that run along
    /// that side.
    /// </summary>
    /// <param name="backwards">Counts from the far end of the lines: the right side, or the bottom.</param>
    private static Side Inward(Counts counts, double reach, bool backwards)
    {
        var length = counts.Far.Length;
        var across = counts.Across;
        if (across < 8 || counts.Within < 4) return default;

        int At(int i) => backwards ? length - 1 - i : i;
        int Far(int i) => counts.Far[At(i)];
        int Held(int i) => counts.Held[At(i)];

        var most = (int)(length * MostCutOff);
        var band = across * BandShare;
        // How far behind a strip of paper something that runs along the side is still looked for: the line of a
        // fold that the cut between two pages missed by a little.
        var ahead = Math.Max(1, (int)Math.Round(length * Mix(0.004, 0.05, reach)));
        var blur = Math.Max(2, (int)Math.Round(length * 0.008));

        var at = 0;
        var cut = false;
        var open = false;
        while (true)
        {
            var next = -1;
            for (var i = at; i <= at + ahead && i < most; i++)
                if (Far(i) > band)
                {
                    next = i;
                    break;
                }

            if (next < 0) break;
            var end = next;
            while (end < most && Far(end) > band) end++;
            // More than can be given up, or of many shades: this is no band around a page but something on it,
            // a photograph that runs off its edge.
            if (end >= most || !OneShade(counts, At(next), At(end - 1)))
            {
                open = true;
                break;
            }

            // A band ends sharply, with a few blurred lines. A sheet lying askew leaves a wedge that thins out slowly,
            // and of that only what is table from end to end goes here.
            var faded = end;
            while (faded < most && faded - end < blur && Far(faded) > across * 0.15) faded++;
            at = Far(faded) <= across * 0.15 ? faded : end;
            cut = true;
        }

        // One line more, so the rim of the band does not show on the page.
        if (cut) at = Math.Min(most, at + 1);

        // What else hangs on to the edge: the soft rim of a shadow, a sliver of the next page, the rest of a wedge.
        // It goes when clean paper follows within reach, and stays as it is otherwise.
        var clean = Math.Max(1, (int)(counts.Within * 0.004));
        var gap = Math.Max(3, (int)Math.Round(length * Mix(0.025, 0.010, reach)));
        var far = Math.Max(1, (int)Math.Round(length * Mix(0.008, 0.075, reach)));

        // Paper with nothing on it, not just nothing that hangs on to the edge: the letters after one that touches
        // the edge are not the page's margin.
        bool Clear(int from)
        {
            for (var i = from; i < from + gap; i++)
                if (i >= length || counts.Marks[At(i)] > clean)
                    return false;
            return true;
        }

        var start = at;
        var limit = Math.Min(start + far, most);
        if (!open)
        {
            // Something soft that runs along the side a little way in (the shadow of a fold) is not stopped short of.
            for (var i = Math.Min(start + Math.Min(ahead, far), limit); i >= start; i--)
                if (Held(i) > across * 0.5)
                {
                    at = i + 1;
                    break;
                }

            // Up to clean paper, but never across print: that is the page, however close to the edge it stands.
            for (var i = at; i <= limit; i++)
            {
                if (Clear(i)) return new Side(i, true, false);
                if (counts.Print[At(i)] > clean) break;
            }
        }

        // Worth a look when something that runs along most of the side is left within reach.
        for (var i = start; i <= limit && !open; i++) open = Held(i) > across * 0.5;
        return new Side(start, false, open);
    }

    /// <summary>
    /// True when the lines of a band are of one shade throughout, as the lid of a scanner, the black of a copy or
    /// a table is, and a photograph is not.
    /// </summary>
    private static bool OneShade(Counts counts, int first, int last)
    {
        if (first > last) (first, last) = (last, first);
        long seen = 0, sum = 0, squares = 0;
        for (var i = first; i <= last; i++)
        {
            seen += counts.Seen[i];
            sum += counts.Shade[i];
            squares += counts.Squares[i];
        }

        if (seen < 16) return true;
        var mean = (double)sum / seen;
        return squares / (double)seen - mean * mean <= 30 * 30;
    }

    /// <summary>The box around what is printed inside part of an area: what is not paper and stands free of the edge.</summary>
    private static Box? Printed(Spot[] spots, int w, int h, int left, int top, int right, int bottom)
    {
        var columns = new int[w];
        var rows = new int[h];
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            if (spots[y * w + x] != Spot.Marked) continue;
            columns[x]++;
            rows[y]++;
        }

        // A speck or two in a line is dust, not print.
        const int least = 2;
        var first = Array.FindIndex(columns, count => count >= least);
        var above = Array.FindIndex(rows, count => count >= least);
        if (first < 0 || above < 0) return null;
        return new Box(first, above, Array.FindLastIndex(columns, count => count >= least) + 1, Array.FindLastIndex(rows, count => count >= least) + 1);
    }

    /// <summary>
    /// Takes the strips set by hand off the pages. A side of a page that faces another page of the sheet loses the
    /// strip set for where pages meet; a side that faces the edge of the sheet loses the strip set for that edge.
    /// </summary>
    private static List<Box> Stripped(List<Box> pages, List<Box> found, int width, int height, SheetEdges edges)
    {
        // Pages lie beside (or above) each other when they share a good part of their height (or width).
        static bool Share(int from, int to, int otherFrom, int otherTo) =>
            Math.Min(to, otherTo) - Math.Max(from, otherFrom) >= 0.3 * Math.Min(to - from, otherTo - otherFrom);

        var result = new List<Box>();
        for (var i = 0; i < pages.Count; i++)
        {
            var own = found[i];
            double middleX = (own.Left + own.Right) / 2.0, middleY = (own.Top + own.Bottom) / 2.0;
            var others = found.Where((_, j) => j != i).ToList();
            var besideLeft = others.Any(o => o.Right <= middleX && Share(own.Top, own.Bottom, o.Top, o.Bottom));
            var besideRight = others.Any(o => o.Left >= middleX && Share(own.Top, own.Bottom, o.Top, o.Bottom));
            var above = others.Any(o => o.Bottom <= middleY && Share(own.Left, own.Right, o.Left, o.Right));
            var below = others.Any(o => o.Top >= middleY && Share(own.Left, own.Right, o.Left, o.Right));

            var page = pages[i];
            // However much is asked for, a good part of the page stays.
            int mostAcross = (int)(page.Width * 0.4), mostDown = (int)(page.Height * 0.4);
            int Strip(double share, int of, int most) => Math.Min(most, (int)Math.Round(share * of));
            result.Add(new Box(
                page.Left + Strip(besideLeft ? edges.Between : edges.Left, width, mostAcross),
                page.Top + Strip(above ? edges.Between : edges.Top, height, mostDown),
                page.Right - Strip(besideRight ? edges.Between : edges.Right, width, mostAcross),
                page.Bottom - Strip(below ? edges.Between : edges.Bottom, height, mostDown)));
        }

        return result;
    }

    // ---- putting them in order ----------------------------------------------------------------------------------

    /// <summary>
    /// The order in which the areas become pages: the indexes of <paramref name="areas"/>, first page first.
    /// <paramref name="numbers"/> are the page numbers read from them (see <see cref="ReadPageNumbers(PdfView,int,IReadOnlyList{RectD},IReadOnlyList{string},CancellationToken)"/>);
    /// they only count for <see cref="SheetOrder.Automatic"/>, where a page without a number follows the numbered one
    /// before it in reading order.
    /// </summary>
    public static IReadOnlyList<int> Order(IReadOnlyList<RectD> areas, SheetOrder order, IReadOnlyList<int?>? numbers = null)
    {
        var reading = InLines(areas, byColumns: order == SheetOrder.Columns, backwards: order == SheetOrder.RowsRightToLeft);
        if (order != SheetOrder.Automatic || numbers == null || numbers.Count != areas.Count || numbers.All(n => n == null)) return reading;

        var keyed = new List<(int Index, int Number, int Unnumbered, int Place)>();
        var last = int.MinValue;
        for (var place = 0; place < reading.Count; place++)
        {
            var index = reading[place];
            if (numbers[index] is { } number)
            {
                last = number;
                keyed.Add((index, number, 0, place));
            }
            else
            {
                keyed.Add((index, last, 1, place));
            }
        }

        return keyed.OrderBy(k => k.Number).ThenBy(k => k.Unnumbered).ThenBy(k => k.Place).Select(k => k.Index).ToList();
    }

    private static List<int> InLines(IReadOnlyList<RectD> areas, bool byColumns, bool backwards)
    {
        // Seen along the direction the lines are stacked in: rows from the top, columns from the left.
        (double Start, double End, double Along) Span(int i) => byColumns
            ? (areas[i].Left, areas[i].Right, areas[i].Top)
            : (areas[i].Top, areas[i].Bottom, backwards ? -areas[i].Left : areas[i].Left);

        var remaining = Enumerable.Range(0, areas.Count).OrderBy(i => Span(i).Start).ToList();
        var result = new List<int>();
        while (remaining.Count > 0)
        {
            var first = Span(remaining[0]);
            // Whatever shares at least half of its extent with the first belongs to the same row (or column).
            var line = remaining.Where(i =>
            {
                var span = Span(i);
                var shared = Math.Min(first.End, span.End) - Math.Max(first.Start, span.Start);
                return shared >= 0.5 * Math.Min(first.End - first.Start, span.End - span.Start);
            }).ToList();
            result.AddRange(line.OrderBy(i => Span(i).Along));
            remaining.RemoveAll(line.Contains);
        }

        return result;
    }

    // ---- page numbers -------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^[^\p{L}\p{N}]*(\d{1,4})[^\p{L}\p{N}]*$")]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:page|pagina|página|pág|pag|pg|seite|sayfa|страница|стр)[\s.:#]*(\d{1,4})(?!\p{N})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelledNumber();

    [GeneratedRegex(@"(?<![\p{N}/.])(\d{1,4})\s*(?:ページ|頁)")]
    private static partial Regex NumberBeforeLabel();

    [GeneratedRegex(@"(?<![\p{N}/.])(\d{1,4})\s*(?:/|of|von|de|sur|из|di)\s*(\d{1,4})(?![\p{N}/.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberOfTotal();

    /// <summary>
    /// Reads the page number printed at the foot or the head of each area of a page, null where none is found.
    /// The page is looked at as a picture, so this works for scans and for pages of real text alike.
    /// </summary>
    /// <exception cref="OcrException">Text recognition is not available.</exception>
    public static IReadOnlyList<int?> ReadPageNumbers(PdfView view, int pageIndex, IReadOnlyList<RectD> areas,
        IReadOnlyList<string> languages, CancellationToken cancel = default)
    {
        var info = view.Pages[pageIndex];
        var scale = Math.Clamp(3600 / Math.Max(1, Math.Max(info.Width, info.Height)), 1, 6.25);
        using var bitmap = view.Render(pageIndex, scale);
        double fx = bitmap.Width / info.Width, fy = bitmap.Height / info.Height;
        return ReadPageNumbers(bitmap, areas.Select(a => new RectD(a.Left * fx, a.Top * fy, a.Right * fx, a.Bottom * fy)).ToList(),
            languages, cancel);
    }

    /// <summary>As above for a picture; the areas are in pixels.</summary>
    public static IReadOnlyList<int?> ReadPageNumbers(SKBitmap image, IReadOnlyList<RectD> areas, IReadOnlyList<string> languages,
        CancellationToken cancel = default)
    {
        var codes = languages.Where(Languages.IsSupported).Select(c => c.ToLowerInvariant()).Distinct().ToList();
        if (codes.Count == 0) codes.Add(Languages.Default);
        var backend = OcrBackends.Get();
        if (OcrBackends.Missing(backend, codes) is { Count: > 0 } missing) throw new OcrLanguageMissingException(missing);
        var list = string.Join('+', codes.Select(c => Languages.Get(c).TesseractCode));

        SKBitmap? converted = null;
        try
        {
            var source = image.ColorType == SKColorType.Bgra8888 ? image : converted = image.Copy(SKColorType.Bgra8888);
            var numbers = new int?[areas.Count];
            for (var i = 0; i < areas.Count; i++) numbers[i] = NumberIn(source, areas[i], backend, list, cancel);
            return numbers;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static int? NumberIn(SKBitmap image, RectD area, IOcrBackend backend, string languages, CancellationToken cancel)
    {
        // A little inside the area: its edge may still show a sliver of what lies around the page.
        double insetX = area.Width * 0.01, insetY = area.Height * 0.01;
        int left = Math.Clamp((int)(area.Left + insetX), 0, image.Width - 1), right = Math.Clamp((int)(area.Right - insetX), left + 1, image.Width);
        int top = Math.Clamp((int)(area.Top + insetY), 0, image.Height - 1), bottom = Math.Clamp((int)(area.Bottom - insetY), top + 1, image.Height);
        var band = Math.Min(bottom - top, Math.Max(48, (int)((bottom - top) * 0.13)));
        (int Number, double Score)? best = null;

        // The foot of the page first: that is where most page numbers are, and a clear one there settles it.
        foreach (var foot in new[] { true, false })
        {
            var from = foot ? bottom - band : top;
            using var strip = Part(image, left, from, right, from + band);
            foreach (var (snippet, nearEdge) in Snippets(strip, foot))
            {
                using (snippet)
                {
                    cancel.ThrowIfCancellationRequested();
                    var words = backend.Recognize(snippet, languages, 300, singleLine: true)
                        .SelectMany(line => line.Words).Where(word => word.Confidence >= 30).Select(word => word.Text);
                    if (Candidate(string.Join(' ', words)) is not var (number, score)) continue;
                    // Of two that look alike, the one nearer the edge of the page is the page number.
                    score += (foot ? 0.5 : 0) + 0.4 * nearEdge;
                    if (best == null || score > best.Value.Score) best = (number, score);
                }
            }

            if (best is { Score: >= 3 }) break;
        }

        return best?.Number;
    }

    /// <summary>
    /// The pieces of writing in the strip along the head or the foot of a page, each cut out as a picture of one
    /// line, with how near it is to the edge of the page (0-1). A number standing alone has to be shown to the
    /// reader on its own: looking at the strip as a page, it does not see text in a single digit.
    /// </summary>
    private static List<(SKBitmap Picture, double NearEdge)> Snippets(SKBitmap strip, bool foot)
    {
        int w = strip.Width, h = strip.Height;
        var pixels = (uint*)strip.GetPixels();
        var stride = strip.RowBytes / 4;
        var luma = new byte[w * h];
        var levels = new int[256];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var value = (byte)ImageAnalysis.LumaOf(pixels[y * stride + x]);
            luma[y * w + x] = value;
            levels[value]++;
        }

        var paper = Array.IndexOf(levels, levels.Max());
        var inkRows = new bool[h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w && !inkRows[y]; x++)
            inkRows[y] = Math.Abs(luma[y * w + x] - paper) > 70;

        // Lines of writing: rows with ink, the nearest to the edge of the page first.
        var lines = new List<(int Top, int Bottom)>();
        for (var y = 0; y < h; y++)
        {
            if (!inkRows[y]) continue;
            var end = y + 1;
            // Two rows without ink do not end a line yet: the dot of an i floats above its stem.
            while (end < h && (inkRows[end] || (end + 1 < h && inkRows[end + 1]) || (end + 2 < h && inkRows[end + 2]))) end++;
            lines.Add((y, end));
            y = end;
        }

        if (foot) lines.Reverse();
        var snippets = new List<(SKBitmap, double)>();
        foreach (var (top, bottom) in lines.Where(line => line.Bottom - line.Top >= 8 && line.Bottom - line.Top <= h * 0.45).Take(3))
        {
            var height = bottom - top;
            // Words belong together; what stands far apart on the line (a title on the left, the number on the
            // right) is read separately.
            var groups = new List<(int Left, int Right)>();
            int start = -1, last = -1;
            for (var x = 0; x <= w; x++)
            {
                var any = false;
                for (var y = top; x < w && y < bottom && !any; y++) any = Math.Abs(luma[y * w + x] - paper) > 70;
                if (any)
                {
                    if (start < 0) start = x;
                    last = x;
                }
                else if (start >= 0 && (x == w || x - last > height * 2.2))
                {
                    groups.Add((start, last + 1));
                    start = -1;
                }
            }

            foreach (var (groupLeft, groupRight) in groups.Where(g => g.Right - g.Left >= height * 0.2).Take(4))
            {
                var pad = Math.Max(6, height / 2);
                var size = Math.Clamp(56.0 / height, 0.5, 4);
                var picture = new SKBitmap(new SKImageInfo(Math.Max(1, (int)((groupRight - groupLeft + 2 * pad) * size)),
                    Math.Max(1, (int)((height + 2 * pad) * size)), SKColorType.Bgra8888, SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(picture))
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                {
                    canvas.Clear(new SKColor((byte)paper, (byte)paper, (byte)paper));
                    canvas.DrawBitmap(strip, new SKRect(groupLeft, top, groupRight, bottom),
                        new SKRect((float)(pad * size), (float)(pad * size), (float)((groupRight - groupLeft + pad) * size), (float)((height + pad) * size)), paint);
                }

                var middle = (top + bottom) / 2.0 / h;
                snippets.Add((picture, foot ? middle : 1 - middle));
            }
        }

        return snippets;
    }

    /// <summary>The page number a line of text states, and how sure that is; null for ordinary text.</summary>
    internal static (int Number, double Score)? Candidate(string line)
    {
        line = line.Trim();
        if (line.Length is 0 or > 60) return null;

        if (NumberOfTotal().Match(line) is { Success: true } ofTotal &&
            int.Parse(ofTotal.Groups[1].Value) is var page and > 0 && int.Parse(ofTotal.Groups[2].Value) is var total &&
            page <= total && total <= 1500)
            return (page, 3.2);
        if (BareNumber().Match(line) is { Success: true } bare && int.Parse(bare.Groups[1].Value) is var alone and > 0) return (alone, 3);
        if (LabelledNumber().Match(line) is { Success: true } labelled && int.Parse(labelled.Groups[1].Value) is var named and > 0) return (named, 2.5);
        if (NumberBeforeLabel().Match(line) is { Success: true } before && int.Parse(before.Groups[1].Value) is var counted and > 0) return (counted, 2.5);
        return null;
    }

    /// <summary>A copy of part of a picture (Gray8 or Bgra8888).</summary>
    private static SKBitmap Part(SKBitmap source, int left, int top, int right, int bottom)
    {
        var part = new SKBitmap(new SKImageInfo(right - left, bottom - top, source.ColorType, source.AlphaType));
        var size = source.BytesPerPixel;
        for (var y = top; y < bottom; y++)
            Buffer.MemoryCopy((byte*)source.GetPixels() + (long)y * source.RowBytes + (long)left * size,
                (byte*)part.GetPixels() + (long)(y - top) * part.RowBytes, part.RowBytes, (long)(right - left) * size);
        return part;
    }

    // ---- cutting ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces each page named in <paramref name="cuts"/> by one page per area listed for it, in that order. The
    /// areas are in visual coordinates of the page. Every new page keeps only what is on its part of the sheet: the
    /// text and drawings there, and of the pictures the pixels that show. With <paramref name="keepSheets"/> the
    /// uncut sheet stays in front of its pages.
    /// </summary>
    public static byte[] Split(byte[] pdf, IReadOnlyDictionary<int, IReadOnlyList<RectD>> cuts, bool keepSheets = false,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var sheets = cuts.Where(c => c.Value.Count > 0).ToDictionary(c => c.Key, c => c.Value);
        if (sheets.Count == 0) return pdf;

        byte[] saved;
        PdfDoc source, target;
        lock (PdfDoc.Sync)
        {
            source = PdfDoc.Load(pdf);
            target = PdfDoc.CreateNew();
        }

        try
        {
            int count;
            lock (PdfDoc.Sync)
            {
                count = source.PageCount;
                foreach (var index in sheets.Keys)
                    if (index < 0 || index >= count)
                        throw new ArgumentOutOfRangeException(nameof(cuts), $"Page {index + 1} does not exist.");
                Native.FPDF_CopyViewerPreferences(target.Handle, source.Handle);
            }

            // Pages that stay as they are, waiting to be copied over in one go.
            var run = new List<int>();

            void CopyRun()
            {
                if (run.Count == 0) return;
                lock (PdfDoc.Sync) target.ImportPages(source, run, target.PageCount);
                run.Clear();
            }

            var done = 0;
            for (var index = 0; index < count; index++)
            {
                cancel.ThrowIfCancellationRequested();
                if (!sheets.TryGetValue(index, out var areas))
                {
                    run.Add(index);
                    continue;
                }

                if (keepSheets) run.Add(index);
                CopyRun();
                // The lock is taken per sheet so the page view can keep drawing during a long job.
                lock (PdfDoc.Sync)
                {
                    // A small document of its own: rewriting page content costs more the more pages the document has.
                    using var scratch = PdfDoc.CreateNew();
                    for (var piece = 0; piece < areas.Count; piece++)
                    {
                        // Brought over one at a time, every copy gets content and pictures of its own to cut down.
                        // Copied together they would share them, and cutting one would cut them all.
                        scratch.ImportPages(source, [index], piece);
                        CutOut(scratch, piece, areas[piece]);
                    }

                    target.ImportPages(scratch, null, target.PageCount);
                }

                progress?.Report(++done / (double)sheets.Count * 0.9);
            }

            CopyRun();
            lock (PdfDoc.Sync) saved = target.Save();
        }
        finally
        {
            lock (PdfDoc.Sync)
            {
                target.Dispose();
                source.Dispose();
            }
        }

        // The uncut pictures are no longer drawn by anything; pictures that were left whole are shared again.
        var result = ImageObjects.DropReplacedImages(saved);
        progress?.Report(1);
        return result;
    }

    private static void CutOut(PdfDoc doc, int index, RectD area)
    {
        using (var sheet = doc.LoadPage(index))
        {
            double left = Math.Clamp(area.Left, 0, sheet.Width), right = Math.Clamp(area.Right, 0, sheet.Width);
            double top = Math.Clamp(area.Top, 0, sheet.Height), bottom = Math.Clamp(area.Bottom, 0, sheet.Height);
            if (right - left < 1 || bottom - top < 1) throw new PdfException("The area to cut out is empty.");
            var m = sheet.VisualToPage;
            var (x0, y0) = m.Apply(left, top);
            var (x1, y1) = m.Apply(right, bottom);
            float l = (float)Math.Min(x0, x1), b = (float)Math.Min(y0, y1), r = (float)Math.Max(x0, x1), t = (float)Math.Max(y0, y1);
            Native.FPDFPage_SetMediaBox(sheet.Handle, l, b, r, t);
            Native.FPDFPage_SetCropBox(sheet.Handle, l, b, r, t);
        }

        // Loaded anew, the page has the size of its part of the sheet. What lies beyond that can go.
        using var page = doc.LoadPage(index);
        if (DropHidden(page)) page.GenerateContent();
    }

    /// <summary>
    /// Removes what cannot be seen on a page: objects that lie outside it altogether, and of the pictures that reach
    /// beyond it the pixels out there. True when something was changed.
    /// </summary>
    internal static bool DropHidden(PdfPage page)
    {
        double width = page.Width, height = page.Height;
        var toVisual = page.PageToVisual;
        var changed = false;
        foreach (var obj in page.GetObjects())
        {
            float l, b, r, t;
            if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) continue;
            var bounds = toVisual.MapBounds(l, b, r, t);
            if (bounds.Right < -0.5 || bounds.Bottom < -0.5 || bounds.Left > width + 0.5 || bounds.Top > height + 0.5)
            {
                page.RemoveObject(obj);
                changed = true;
                continue;
            }

            if (Native.FPDFPageObj_GetType(obj) != Native.FPDF_PAGEOBJ_IMAGE || PdfPageObjects.IsEditable(obj)) continue;
            if (TrimPicture(page, obj, width, height, toVisual)) changed = true;
        }

        return changed;
    }

    private static bool TrimPicture(PdfPage page, nint obj, double width, double height, Affine toVisual)
    {
        FS_MATRIX matrix;
        if (Native.FPDFPageObj_GetMatrix(obj, &matrix) == 0) return false;
        var placed = new Affine(matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f);
        var shown = placed.Then(toVisual);
        var size = shown.Scale;
        if (size < 1e-6) return false;
        // Only pictures that stand straight or on their side: of a tilted one no rectangle of pixels is what shows.
        var straight = Math.Abs(shown.B) < size * 1e-3 && Math.Abs(shown.C) < size * 1e-3;
        var onItsSide = Math.Abs(shown.A) < size * 1e-3 && Math.Abs(shown.D) < size * 1e-3;
        if (!straight && !onItsSide) return false;

        // The page as the picture sees it: the picture itself is the square from (0,0) to (1,1), y pointing up.
        var back = shown.Invert();
        var seen = RectD.FromPoints(back.Apply(0, 0), back.Apply(width, 0), back.Apply(0, height), back.Apply(width, height));
        double u0 = Math.Max(0, seen.Left), u1 = Math.Min(1, seen.Right), v0 = Math.Max(0, seen.Top), v1 = Math.Min(1, seen.Bottom);
        if (u1 <= u0 || v1 <= v0 || (u1 - u0) * (v1 - v0) > 0.92) return false;
        if (!ImageObjects.IsRecompressible(page, obj, out _, out _)) return false;

        using var pixels = ImageObjects.GetPixels(obj);
        if (pixels == null) return false;
        int pw = pixels.Width, ph = pixels.Height;
        // One pixel more on every side, so the edge of the page is not drawn from a blend with nothing.
        int x0 = Math.Max(0, (int)Math.Floor(u0 * pw) - 1), x1 = Math.Min(pw, (int)Math.Ceiling(u1 * pw) + 1);
        int y0 = Math.Max(0, (int)Math.Floor((1 - v1) * ph) - 1), y1 = Math.Min(ph, (int)Math.Ceiling((1 - v0) * ph) + 1);
        if (x1 - x0 < 1 || y1 - y0 < 1 || (long)(x1 - x0) * (y1 - y0) > 0.92 * pw * ph) return false;

        var index = page.GetObjects().IndexOf(obj);
        var fresh = Native.FPDFPageObj_NewImageObj(page.Doc.Handle);
        if (index < 0 || fresh == 0) return false;
        try
        {
            using var part = Part(pixels, x0, y0, x1, y1);
            // A photograph goes back as a photograph; what was stored exactly stays exact.
            if (IsLossy(obj)) PageDraw.SetJpeg(page, fresh, ImageFilters.EncodeJpeg(part, 92));
            else PageDraw.SetBitmap(page, fresh, part);
        }
        catch (PdfException)
        {
            Native.FPDFPageObj_Destroy(fresh);
            return false;
        }

        var within = new Affine((double)(x1 - x0) / pw, 0, 0, (double)(y1 - y0) / ph, (double)x0 / pw, 1 - (double)y1 / ph);
        var m = within.Then(placed);
        Native.FPDFImageObj_SetMatrix(fresh, m.A, m.B, m.C, m.D, m.E, m.F);

        // The new picture takes the place of the old one only if the page looks the same with it: a picture can be
        // shown through a mask, a clip or a blend that its pixels alone do not tell.
        var scale = Math.Min(4, 1400 / Math.Max(1, Math.Max(width, height)));
        using var before = page.Render(scale);
        Native.FPDFPage_InsertObjectAtIndex(page.Handle, fresh, (nuint)index);
        Native.FPDFPageObj_SetIsActive(obj, 0);
        using var after = page.Render(scale);
        if (ImageAnalysis.BlockDifference(before, after) < 2.5)
        {
            page.RemoveObject(obj);
            return true;
        }

        Native.FPDFPageObj_SetIsActive(obj, 1);
        page.RemoveObject(fresh);
        return false;
    }

    private static bool IsLossy(nint image)
    {
        var count = Native.FPDFImageObj_GetImageFilterCount(image);
        var name = stackalloc byte[64];
        for (var i = 0; i < count; i++)
        {
            var length = (int)Native.FPDFImageObj_GetImageFilter(image, i, name, new CULong(64)).Value;
            if (length is <= 1 or > 64) continue;
            if (Encoding.ASCII.GetString(name, length - 1) is "DCTDecode" or "JPXDecode") return true;
        }

        return false;
    }
}
