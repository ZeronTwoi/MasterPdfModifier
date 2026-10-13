using PdfModifier.Core.Fonts;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Pdfium;
using PdfModifier.Core.Text;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

/// <summary>How much trouble is taken over reading a page.</summary>
public enum OcrEffort
{
    /// <summary>One reading, and a second look where much was left unread or read without certainty.</summary>
    Standard,

    /// <summary>
    /// On top of that the page is read again brought into focus and enlarged, and again reduced to black and white;
    /// every line keeps the reading that is surest. Several times slower; for scans that are blurred, coarse or pale.
    /// </summary>
    Careful,

    /// <summary>
    /// Everything <see cref="Careful"/> does and more of it: focused weakly, firmly and hard, and with the strokes
    /// made heavier. Affordable for a marked passage, far too slow for whole pages.
    /// </summary>
    Exhaustive
}

/// <summary>
/// Which languages to look for. With <see cref="AutoDetect"/> and several languages selected, a quick first pass decides
/// which of them the page is actually written in and the full pass uses only those.
/// </summary>
public sealed record OcrSettings(IReadOnlyList<string> Languages, bool AutoDetect = true, int Dpi = 300)
{
    /// <summary>Words read with less confidence than this (0-100) are treated as noise.</summary>
    public double MinimumConfidence { get; init; } = 25;

    /// <summary>
    /// Looks a second time, for scattered text, when the first reading leaves much of what is printed on the page
    /// unexplained. Forms, tables and labelled drawings need this; plain pages of text are not slowed down by it.
    /// </summary>
    public bool Thorough { get; init; } = true;

    public OcrEffort Effort { get; init; }

    public static OcrSettings DefaultFor(params string[] languages) => new(languages.Length == 0 ? [Text.Languages.Default] : languages);
}

/// <summary>A recognised word. Bounds are in visual page coordinates (points, origin top-left).</summary>
public sealed record OcrWord(string Text, RectD Bounds, double Confidence);

public sealed record OcrLine(string Text, RectD Bounds, IReadOnlyList<OcrWord> Words);

public sealed record OcrPageResult(IReadOnlyList<OcrLine> Lines, IReadOnlyList<string> DetectedLanguages)
{
    public string Text => string.Join('\n', Lines.Select(l => l.Text));
}

public static class PdfOcr
{
    /// <summary>Share of the printed marks outside every recognised line from which a second, scattered reading is made.</summary>
    private const double UnreadLimit = 0.2;

    /// <summary>Average confidence below which a page is read a second time after cleaning it up.</summary>
    private const double UnsureLimit = 90;

    public static (bool Available, string Detail) EngineStatus() => OcrBackends.Probe();

    /// <summary>Finds and reads the text on one page. The page is rendered, so this works for scans and text pages alike.</summary>
    public static OcrPageResult RecognizePage(byte[] pdf, int pageIndex, OcrSettings settings, CancellationToken cancel = default)
    {
        using var view = new PdfView(pdf);
        return RecognizePage(view, pageIndex, settings, cancel);
    }

    /// <param name="firstLook">
    /// Called with what the first reading found when a second, closer one is about to follow. Someone waiting for
    /// the result can start working with this while the rest is still being read.
    /// </param>
    public static OcrPageResult RecognizePage(PdfView view, int pageIndex, OcrSettings settings, CancellationToken cancel = default,
        Action<OcrPageResult>? firstLook = null)
    {
        var scale = settings.Dpi / 72.0;
        // Without the text boxes the app added: they are text already and must not be read back as part of the scan.
        using var bitmap = view.Render(pageIndex, scale, withoutEditable: true);
        cancel.ThrowIfCancellationRequested();
        return Recognize(bitmap, settings, 1 / scale, firstLook);
    }

    /// <summary>Recognises text in an image. <paramref name="unitsPerPixel"/> converts pixel boxes into the caller's units.</summary>
    public static OcrPageResult Recognize(SKBitmap image, OcrSettings settings, double unitsPerPixel = 1, Action<OcrPageResult>? firstLook = null)
    {
        var codes = settings.Languages.Where(Languages.IsSupported).Select(c => c.ToLowerInvariant()).Distinct().ToList();
        if (codes.Count == 0) codes.Add(Languages.Default);

        var backend = OcrBackends.Get();
        if (backend.NeedsLocalData && TessData.Missing(codes) is { Count: > 0 } missing)
            throw new OcrLanguageMissingException(missing);

        var use = codes;
        if (settings.AutoDetect && codes.Count > 1)
        {
            // Recognising with many languages at once is slow and less accurate, so find out which ones matter first.
            var largest = Math.Max(image.Width, image.Height);
            var factor = Math.Min(1.0, 1600.0 / largest);
            using var small = factor < 1
                ? ImageFilters.Resize(image, Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)))
                : null;
            var preview = backend.Recognize(small ?? image, Join(codes), Math.Max(70, (int)(settings.Dpi * factor)));
            var previewText = string.Join(' ', preview.SelectMany(l => l.Words).Where(w => w.Confidence >= settings.MinimumConfidence).Select(w => w.Text));
            var ranked = LanguageDetector.Detect(previewText, codes);
            var detected = ranked.Where(r => r.Score >= 0.4).Take(3).Select(r => r.Code).ToList();
            if (detected.Count > 0) use = detected;
        }

        var languageList = Join(use);
        var lines = ToLines(backend.Recognize(image, languageList, settings.Dpi), settings.MinimumConfidence, unitsPerPixel);
        if (settings.Thorough && (UnreadShare(image, lines, unitsPerPixel) > UnreadLimit || Certainty(lines) < UnsureLimit))
        {
            // Much of what is printed was not read, or read with little certainty. Usually the page is unevenly lit or
            // speckled (a photograph, a bad copy), which is dealt with first; the better of the two readings is kept
            // and completed from the other.
            firstLook?.Invoke(new OcrPageResult(lines.ToList(), use));
            using var cleaned = Clean(image);
            lines = Merge(lines, ToLines(backend.Recognize(cleaned, languageList, settings.Dpi), settings.MinimumConfidence, unitsPerPixel));
            if (UnreadShare(cleaned, lines, unitsPerPixel) > UnreadLimit)
            {
                // Read as loose words, the page gives up labels the block analysis passed over. Scattered reading also
                // sees "text" in pictures and rules, so only what is read with some certainty is taken from it.
                var scattered = ToLines(backend.Recognize(cleaned, languageList, settings.Dpi, sparse: true),
                    Math.Max(settings.MinimumConfidence, 45), unitsPerPixel);
                lines = Merge(lines, scattered.Where(line => line.Text.Count(char.IsLetterOrDigit) >= 2).ToList(), keepFirst: true);
            }
        }

        if (settings.Effort != OcrEffort.Standard) lines = ReadAgain(image, lines, backend, languageList, settings, unitsPerPixel);

        var text = string.Join(' ', lines.Select(l => l.Text));
        var languages = use.Count == 1 ? use : LanguageDetector.Detect(text, use).Where(r => r.Score >= 0.4).Select(r => r.Code).ToList();
        return new OcrPageResult(lines, languages.Count > 0 ? languages : use);
    }

    /// <summary>
    /// Below this certainty (see <see cref="Certainty(OcrLine)"/>) a line was more guessed at than read, and is worth
    /// a second look by whoever is going to rely on it.
    /// </summary>
    public const double DoubtfulBelow = 70;

    /// <summary>How surely a line was read (0-100): the confidence of its words, weighted by their length.</summary>
    public static double Certainty(OcrLine line) => Certainty([line]);

    /// <summary>
    /// Reads one area of a page in every way there is (<see cref="OcrEffort.Exhaustive"/>) and with nothing else of
    /// the page to distract from it. For the passage of a poor scan that came out wrong or was not found at all. The
    /// lines come back in the coordinates of the page.
    /// </summary>
    public static OcrPageResult RecognizeArea(PdfView view, int pageIndex, RectD area, OcrSettings settings, CancellationToken cancel = default)
    {
        var page = view.Pages[pageIndex];
        area = new RectD(Math.Max(0, area.Left), Math.Max(0, area.Top), Math.Min(page.Width, area.Right), Math.Min(page.Height, area.Bottom));
        if (area.Width < 2 || area.Height < 2) return new OcrPageResult([], []);
        // At the resolution pages are read at. Drawn larger, a scan gains nothing but the size of its blur, and print
        // much larger than a reader expects is read worse, not better; small print is enlarged where that helps.
        var scale = settings.Dpi / 72.0;
        using var bitmap = view.RenderArea(pageIndex, area, scale, withoutEditable: true);
        cancel.ThrowIfCancellationRequested();
        var read = Recognize(bitmap, settings with { Effort = OcrEffort.Exhaustive }, 1 / scale);
        return new OcrPageResult(read.Lines.Select(line => Moved(line, area.Left, area.Top)).ToList(), read.DetectedLanguages);
    }

    private static OcrLine Moved(OcrLine line, double dx, double dy)
    {
        static RectD Shift(RectD box, double dx, double dy) => new(box.Left + dx, box.Top + dy, box.Right + dx, box.Bottom + dy);
        return new OcrLine(line.Text, Shift(line.Bounds, dx, dy), line.Words.Select(w => w with { Bounds = Shift(w.Bounds, dx, dy) }).ToList());
    }

    /// <summary>
    /// Reads the page again in other lights and lets every line keep the reading that is surest: once brought into
    /// focus (and enlarged, where the print is small), once reduced to pure black and white.
    /// </summary>
    private static List<OcrLine> ReadAgain(SKBitmap image, List<OcrLine> lines, IOcrBackend backend, string languages, OcrSettings settings,
        double unitsPerPixel)
    {
        // Print is small when its lines are low: capital letters of less than about twenty pixels leave little to read.
        var heights = lines.Select(line => line.Bounds.Height / unitsPerPixel).Order().ToList();
        var small = heights.Count == 0 || heights[heights.Count / 2] < 30;
        // How blurred a page is cannot be told beforehand, and focusing it too hard is as bad as too little. For a
        // whole page one middling attempt has to do; a passage is tried weakly, firmly and hard.
        Func<SKBitmap, SKBitmap>[] ways = settings.Effort == OcrEffort.Exhaustive
            ?
            [
                picture => Focused(picture, small, 0.3), picture => Focused(picture, small, 0.6), picture => Focused(picture, small, 0.85),
                picture => Focused(picture, small, 0.45, heavier: 0.5), TwoTone
            ]
            : [picture => Focused(picture, small, 0.45), TwoTone];
        foreach (var prepare in ways)
        {
            using var variant = prepare(image);
            var grown = (double)variant.Width / image.Width;
            var read = ToLines(backend.Recognize(variant, languages, (int)Math.Round(settings.Dpi * grown)), settings.MinimumConfidence,
                unitsPerPixel / grown);
            lines = Surest(lines, read);
        }

        return InReadingOrder(lines);
    }

    /// <summary>
    /// The page with its lighting evened out, enlarged if its print is small, brought into focus by
    /// <paramref name="strength"/> (0-1) and, with <paramref name="heavier"/>, its strokes made bolder.
    /// </summary>
    private static SKBitmap Focused(SKBitmap image, bool enlarge, double strength, double heavier = 0)
    {
        using var larger = enlarge && (long)image.Width * image.Height <= 10_000_000
            ? ImageFilters.Resize(image, image.Width * 2, image.Height * 2)
            : null;
        var flat = ImageFilters.FlattenBackground(PlanarImage.FromBitmap(larger ?? image, true));
        // Enlarged, the blur is twice as wide in pixels as it was.
        ImageFilters.Deblur(flat, larger != null ? Math.Min(1, strength * 1.6) : strength);
        ImageFilters.WhitenBackground(flat, 0.1);
        if (heavier > 0) ImageFilters.Thicken(flat, heavier);
        return flat.ToBitmap();
    }

    /// <summary>The page in pure black and white, each spot judged against its surroundings.</summary>
    private static SKBitmap TwoTone(SKBitmap image)
    {
        var flat = ImageFilters.FlattenBackground(PlanarImage.FromBitmap(image, true));
        ImageFilters.Denoise(flat);
        ImageFilters.Binarize(flat);
        return flat.ToBitmap();
    }

    /// <summary>
    /// Joins two readings line by line. A line of the second takes the place of what it lies over in the first when
    /// it reads about as much, more surely; where the first has nothing, it is added if it was read with some certainty.
    /// </summary>
    private static List<OcrLine> Surest(List<OcrLine> known, List<OcrLine> other)
    {
        static int Letters(OcrLine line) => line.Words.Sum(w => w.Text.Count(char.IsLetterOrDigit));

        var result = known.ToList();
        foreach (var line in other)
        {
            var letters = Letters(line);
            if (letters == 0) continue;
            var rivals = result.Where(k => Overlap(k.Bounds, line.Bounds) > 0.4).ToList();
            if (rivals.Count == 0)
            {
                if (letters >= 2 && Certainty(line) >= 60) result.Add(line);
                continue;
            }

            if (Certainty(line) > Certainty(rivals) + 3 && letters >= rivals.Sum(Letters) * 0.7)
            {
                result.RemoveAll(rivals.Contains);
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>Average confidence (0-100) of what was read, weighted by word length; 100 for an empty page.</summary>
    internal static double Certainty(List<OcrLine> lines)
    {
        double letters = 0, sum = 0;
        foreach (var word in lines.SelectMany(line => line.Words))
        {
            var n = word.Text.Count(char.IsLetterOrDigit);
            letters += n;
            sum += n * word.Confidence;
        }

        return letters == 0 ? 100 : sum / letters;
    }

    /// <summary>The page with its lighting evened out and speckles removed, as a grayscale picture.</summary>
    private static SKBitmap Clean(SKBitmap image)
    {
        var flat = ImageFilters.FlattenBackground(PlanarImage.FromBitmap(image, true));
        ImageFilters.Denoise(flat);
        return flat.ToBitmap();
    }

    /// <summary>
    /// Joins two readings of the same page. The one that read more, more surely, is the base; lines of the other are
    /// added where the base has nothing. With <paramref name="keepFirst"/> the first reading is the base regardless.
    /// </summary>
    private static List<OcrLine> Merge(List<OcrLine> first, List<OcrLine> second, bool keepFirst = false)
    {
        static double Weight(List<OcrLine> lines) =>
            lines.SelectMany(l => l.Words).Sum(w => w.Text.Count(char.IsLetterOrDigit) * w.Confidence);

        var (basis, other) = keepFirst || Weight(first) >= Weight(second) ? (first, second) : (second, first);
        var merged = basis.ToList();
        merged.AddRange(other.Where(line => !basis.Any(known => Overlap(known.Bounds, line.Bounds) > 0.3)));
        return merged.Count == basis.Count ? basis : InReadingOrder(merged);
    }

    private static List<OcrLine> ToLines(List<RawLine> raw, double minimumConfidence, double unitsPerPixel)
    {
        var lines = new List<OcrLine>();
        foreach (var rawLine in raw)
        {
            var words = rawLine.Words
                .Where(w => w.Confidence >= minimumConfidence && w.Right > w.Left && w.Bottom > w.Top)
                .Select(w => new OcrWord(w.Text,
                    new RectD(w.Left * unitsPerPixel, w.Top * unitsPerPixel, w.Right * unitsPerPixel, w.Bottom * unitsPerPixel), w.Confidence))
                .ToList();
            if (words.Count == 0) continue;
            var bounds = words.Select(w => w.Bounds).Aggregate((a, b) => a.Union(b));
            lines.Add(new OcrLine(JoinWords(words), bounds, words));
        }

        return lines;
    }

    /// <summary>How much of the smaller of two boxes lies inside the other (0-1).</summary>
    private static double Overlap(RectD a, RectD b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (width <= 0 || height <= 0) return 0;
        return width * height / Math.Max(1e-9, Math.Min(a.Width * a.Height, b.Width * b.Height));
    }

    /// <summary>Top to bottom; lines at about the same height left to right.</summary>
    private static List<OcrLine> InReadingOrder(List<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(l => l.Bounds.CenterY))
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row != null && line.Bounds.CenterY < row.Max(l => l.Bounds.Bottom) && line.Bounds.CenterY > row.Min(l => l.Bounds.Top)) row.Add(line);
            else rows.Add([line]);
        }

        return rows.SelectMany(row => row.OrderBy(l => l.Bounds.Left)).ToList();
    }

    /// <summary>
    /// Share (0-1) of the small dark marks on a page that no recognised word accounts for. Rules, frames and filled
    /// areas are left out of the count: what remains unexplained is writing that was missed, or the detail of a picture.
    /// </summary>
    internal static double UnreadShare(SKBitmap image, List<OcrLine> read, double unitsPerPixel = 1)
    {
        var factor = Math.Min(1.0, 700.0 / Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)(image.Width * factor)), h = Math.Max(1, (int)(image.Height * factor));
        using var small = factor < 1 ? ImageFilters.Resize(image, w, h) : null;
        // With the lighting evened out first: in the shadow of a bad copy, paper and writing are both "dark" and could
        // not be told apart, which is the very case this measure has to catch.
        var gray = ImageFilters.FlattenBackground(PlanarImage.FromBitmap(small ?? image, true)).Planes[0];

        var histogram = new long[256];
        foreach (var v in gray) histogram[v]++;
        var threshold = ImageFilters.OtsuThreshold(histogram);
        long dark = 0;
        for (var i = 0; i < threshold; i++) dark += histogram[i];
        // On a dark page the writing is what is light.
        var inkIsDark = dark <= gray.Length / 2;
        var ink = new bool[gray.Length];
        for (var i = 0; i < gray.Length; i++) ink[i] = gray[i] < threshold == inkIsDark;

        // Long straight runs are rules, frames and filled areas, not letters.
        var longRun = Math.Max(24, Math.Max(w, h) / 18);
        var plain = (bool[])ink.Clone();
        for (var y = 0; y < h; y++)
        for (int x = 0, start = 0; x <= w; x++)
        {
            if (x < w && plain[y * w + x]) continue;
            if (x - start >= longRun) ink.AsSpan(y * w + start, x - start).Clear();
            start = x + 1;
        }

        for (var x = 0; x < w; x++)
        for (int y = 0, start = 0; y <= h; y++)
        {
            if (y < h && plain[y * w + x]) continue;
            if (y - start >= longRun)
                for (var i = start; i < y; i++)
                    ink[i * w + x] = false;
            start = y + 1;
        }

        long total = 0;
        foreach (var mark in ink)
            if (mark)
                total++;
        if (total < gray.Length / 400) return 0;

        var toSmall = factor / unitsPerPixel;
        foreach (var box in read.SelectMany(line => line.Words).Select(word => word.Bounds))
        {
            int x0 = Math.Max(0, (int)(box.Left * toSmall) - 1), x1 = Math.Min(w, (int)Math.Ceiling(box.Right * toSmall) + 1);
            int y0 = Math.Max(0, (int)(box.Top * toSmall) - 1), y1 = Math.Min(h, (int)Math.Ceiling(box.Bottom * toSmall) + 1);
            for (var y = y0; y < y1; y++) ink.AsSpan(y * w + x0, Math.Max(0, x1 - x0)).Clear();
        }

        long unread = 0;
        foreach (var mark in ink)
            if (mark)
                unread++;
        return (double)unread / total;
    }

    /// <summary>
    /// Adds an invisible text layer over the scanned parts of pages so their text can be selected, searched and copied.
    /// A page is read when it has no such layer yet and is a picture, wholly or in part; on pages that mix a scan with
    /// real text only what is not text already is added. With <paramref name="force"/> every page is read in full.
    /// </summary>
    /// <param name="carefulAreas">
    /// Areas, by page, that are read once more on their own with all the care there is (see <see cref="RecognizeArea"/>);
    /// what is found there takes the place of what the reading of the whole page gave for them.
    /// </param>
    public static (byte[] Pdf, int PagesRecognised) MakeSearchable(byte[] pdf, IReadOnlyCollection<int>? pages, OcrSettings settings,
        bool force = false, IProgress<double>? progress = null, CancellationToken cancel = default,
        IReadOnlyDictionary<int, IReadOnlyList<RectD>>? carefulAreas = null)
    {
        var results = new Dictionary<int, IReadOnlyList<OcrLine>>();
        using (var view = new PdfView(pdf))
        {
            var selected = PdfPages.ResolvePages(pages, view.PageCount);
            for (var i = 0; i < selected.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var index = selected[i];
                var content = view.Analyse(index);
                var marked = carefulAreas?.GetValueOrDefault(index) ?? [];
                if (force || NeedsReading(content) || (marked.Count > 0 && content.HiddenText.Count == 0))
                {
                    // A page that has its text and was only marked in places is read in those places alone.
                    IEnumerable<OcrLine> lines = force || NeedsReading(content) ? RecognizePage(view, index, settings, cancel).Lines : [];
                    foreach (var area in marked)
                    {
                        cancel.ThrowIfCancellationRequested();
                        lines = lines.Where(line => !area.Contains(line.Bounds.CenterX, line.Bounds.CenterY))
                            .Concat(RecognizeArea(view, index, area, settings, cancel).Lines).ToList();
                    }

                    // What the page already has as text would otherwise be found twice by a search.
                    if (!force) lines = lines.Where(line => !content.TextAreas.Any(area => Covers(area, line.Bounds)));
                    var added = lines.ToList();
                    if (added.Count > 0) results[index] = added;
                }

                progress?.Report((i + 1.0) / selected.Count * 0.95);
            }
        }

        if (results.Count == 0) return (pdf, 0);

        var searchable = ContentEditor.Run(pdf, results.Keys.Order().ToList(), (page, index, fonts) =>
        {
            foreach (var line in results[index])
            {
                // One size and baseline per line keeps the selection highlight even.
                var size = Math.Max(2, line.Bounds.Height * 0.85);
                var baseline = line.Bounds.Bottom - line.Bounds.Height * 0.2;
                var style = new TextStyle(size, 0xFF000000, Invisible: true);
                foreach (var word in line.Words)
                {
                    var natural = FontResolver.Measure(word.Text, null, false, false, size);
                    if (natural <= 0) continue;
                    var stretch = Math.Clamp(word.Bounds.Width / natural, 0.2, 5);
                    PageDraw.DrawText(page, fonts, word.Text + " ", style, word.Bounds.Left, baseline, 0, stretch);
                }
            }

            return true;
        }, (fonts, _) => fonts.PrepareAll(
            results.Values.SelectMany(lines => lines).SelectMany(line => line.Words).Select(word => word.Text).Append(" "), null),
            cancel: cancel);

        progress?.Report(1);
        return (searchable, results.Count);
    }

    /// <summary>
    /// True for a page whose text, or part of it, exists only as a picture: a scan, a page with large figures, or a
    /// page that shows text without containing any (letters drawn as shapes).
    /// </summary>
    public static bool NeedsReading(PageContent content) =>
        content.HiddenText.Count == 0 && (content.HasPictures || content.VisibleLetters + content.NestedText.Sum(l => l.Text.Length) < 12);

    private static bool Covers(RectD area, RectD line) =>
        area.Contains(line.CenterX, line.CenterY) || Overlap(area, line) > 0.5;

    private static string Join(IEnumerable<string> codes) => string.Join('+', codes.Select(c => Languages.Get(c).TesseractCode));

    /// <summary>Joins words with spaces, except between Japanese characters, which are written without them.</summary>
    private static string JoinWords(IReadOnlyList<OcrWord> words)
    {
        var builder = new System.Text.StringBuilder(words[0].Text);
        for (var i = 1; i < words.Count; i++)
        {
            if (!(IsCjk(words[i - 1].Text[^1]) && IsCjk(words[i].Text[0]))) builder.Append(' ');
            builder.Append(words[i].Text);
        }

        return builder.ToString();
    }

    private static bool IsCjk(char c) => c is >= '　' and <= '鿿' or >= '＀' and <= '￯';
}
