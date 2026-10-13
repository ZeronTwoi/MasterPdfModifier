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
public sealed record SheetFinding(IReadOnlyList<RectD> Pages, double Certainty, bool Doubtful);

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
    public static SheetFinding Find(PdfView view, int pageIndex, double sensitivity = UsualSensitivity) =>
        OnPage(view, pageIndex, raster => Look(raster, Limit(sensitivity)));

    /// <summary>As <see cref="Find(PdfView,int,double)"/> for a picture; the areas are in pixels.</summary>
    public static SheetFinding Find(SKBitmap image, double sensitivity = UsualSensitivity) =>
        OnImage(image, raster => Look(raster, Limit(sensitivity)));

    /// <summary>The pages <see cref="Find(PdfView,int,double)"/> finds at the usual sensitivity.</summary>
    public static IReadOnlyList<RectD> Detect(PdfView view, int pageIndex) => Find(view, pageIndex).Pages;

    public static IReadOnlyList<RectD> Detect(SKBitmap image) => Find(image).Pages;

    /// <summary>
    /// Divides a page into <paramref name="columns"/> × <paramref name="rows"/> equal parts. A cut that falls close
    /// to an empty band or a fold moves into it, so the parts end where the pages do.
    /// </summary>
    public static IReadOnlyList<RectD> Grid(PdfView view, int pageIndex, int columns, int rows) =>
        OnPage(view, pageIndex, raster => Divide(raster, columns, rows)).Pages;

    public static IReadOnlyList<RectD> Grid(SKBitmap image, int columns, int rows) =>
        OnImage(image, raster => Divide(raster, columns, rows)).Pages;

    /// <summary>How much has to speak for a division before it is made (see <see cref="Evidence"/>).</summary>
    private static double Limit(double sensitivity) => 0.85 - 0.7 * Math.Clamp(sensitivity, 0, 1);

    /// <summary>Within this distance of the limit a result is a close call.</summary>
    private const double CloseCall = 0.12;

    private sealed record Found(List<Box> Pages, double Certainty, bool Doubtful);

    private static SheetFinding OnPage(PdfView view, int pageIndex, Func<Raster, Found> find)
    {
        var info = view.Pages[pageIndex];
        var scale = Math.Min(3, WorkSize / Math.Max(1, Math.Max(info.Width, info.Height)));
        using var bitmap = view.Render(pageIndex, scale);
        double fx = info.Width / bitmap.Width, fy = info.Height / bitmap.Height;
        var found = find(Raster.Read(bitmap));
        return new SheetFinding(InOrder(found.Pages.Select(b => new RectD(b.Left * fx, b.Top * fy, b.Right * fx, b.Bottom * fy)).ToList()),
            found.Certainty, found.Doubtful);
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
            double fx = (double)image.Width / source.Width, fy = (double)image.Height / source.Height;
            var found = find(Raster.Read(source));
            return new SheetFinding(InOrder(found.Pages.Select(b => new RectD(b.Left * fx, b.Top * fy, b.Right * fx, b.Bottom * fy)).ToList()),
                found.Certainty, found.Doubtful);
        }
        finally
        {
            small?.Dispose();
            converted?.Dispose();
        }
    }

    private static List<RectD> InOrder(List<RectD> areas) => Order(areas, SheetOrder.Rows).Select(i => areas[i]).ToList();

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

    private static Found Look(Raster raster, double limit)
    {
        var (regions, clear) = Regions(raster);
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

        return new Found(pages, certainty, doubtful);
    }

    private static Found Divide(Raster raster, int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 12);
        rows = Math.Clamp(rows, 1, 12);
        var regions = Regions(raster).Sheets;
        var region = regions.Count == 1 ? regions[0] : new Box(0, 0, raster.Width, raster.Height);
        var profile = new Profile(raster, region);
        // Divided the way someone asked for: there is nothing to doubt about that.
        return new Found(Cells(region, Snapped(profile.Columns, region.Left, region.Width, columns, profile.Sides),
            Snapped(profile.Rows, region.Top, region.Height, rows, profile.Ends)), 1, false);
    }

    /// <summary>
    /// The sheets of paper in the picture: areas that stand out from the background as a whole, and how clearly they
    /// do (0-1). A picture that is one sheet from edge to edge (the usual scan) is a single region covering all of it.
    /// </summary>
    private static (List<Box> Sheets, double Clear) Regions(Raster raster)
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
            if (Sheets(raster) is { Sheets.Count: > 0 } found) return found;
        }

        raster.Threshold(level);
        return ([new Box(0, 0, w, h)], 1);
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
            var gaps = new List<Gap>();
            int leading = 0, trailing = 0;
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

                if (start == 0) leading = end;
                else if (end == counts.Length) trailing = end - start;
                else gaps.Add(new Gap(start + origin, end + origin, lineCount > 0 ? lineSum / lineCount + origin : null));
                start = end;
            }

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
