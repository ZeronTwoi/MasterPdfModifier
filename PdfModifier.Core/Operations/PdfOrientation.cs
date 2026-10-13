using PdfModifier.Core.Imaging;
using PdfModifier.Core.Ocr;
using PdfModifier.Core.Text;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

/// <summary>What the finding about a page's orientation rests on.</summary>
public enum TurnEvidence
{
    /// <summary>Nothing to go by: an empty page, a photograph.</summary>
    None,

    /// <summary>The text the document has on the page: which way its letters stand.</summary>
    Text,

    /// <summary>The page was read as a picture, the way it stands and turned, and one way gave words.</summary>
    Reading,

    /// <summary>The look of its print alone: which way the lines run, and on which side their letters rise above them.</summary>
    Look
}

/// <summary>
/// How a page has to be turned to stand upright: by <see cref="Degrees"/> clockwise (0, 90, 180 or 270), with how
/// much speaks for that (0-1). <see cref="Doubtful"/> marks a close call that someone should look at: a page that was
/// turned on thin evidence, or one that was left as it is although it may well be turned.
/// </summary>
public sealed record PageTurn(int Degrees, double Certainty, bool Doubtful, TurnEvidence Evidence)
{
    public static PageTurn Unknown { get; } = new(0, 0, false, TurnEvidence.None);

    /// <summary>
    /// The turn the evidence points to, whether or not it was enough to act on. With it the same finding can be
    /// judged again by another limit without looking at the page once more (<see cref="PdfOrientation.At"/>).
    /// </summary>
    public int Leaning { get; init; }

    public bool IsTurned => Degrees != 0;
}

/// <summary>
/// Finds pages that lie on their side or stand on their head. A page of text says so itself: its letters have a
/// direction. A scan has to be looked at: which way its lines of print run shows whether it lies on its side, and
/// reading it both ways round shows which of the two it is. Without text recognition the letters themselves are
/// the clue: in most scripts more of them rise above the line than hang below it.
/// </summary>
public static class PdfOrientation
{
    /// <summary>The readiness at which a page is only turned when the evidence is clear.</summary>
    public const double UsualReadiness = 0.5;

    /// <summary>Longer side, in pixels, of the picture a page is looked at in.</summary>
    private const int WorkSize = 1400;

    /// <summary>
    /// How the page has to be turned to stand upright. <paramref name="readiness"/> (0-1) says how readily a page is
    /// taken to be turned: at 0 only where everything speaks for it, at 1 on a slight lead.
    /// </summary>
    /// <param name="languages">The languages the print may be in (app codes); English when none are given.</param>
    /// <param name="read">
    /// Whether scans may be read by text recognition. Without it, and where it is not available, a scan is judged by
    /// the look of its print: that tells a page on its side, and for Latin script also which way up it stands.
    /// </param>
    public static PageTurn Find(PdfView view, int pageIndex, IReadOnlyList<string>? languages = null, bool read = true,
        double readiness = UsualReadiness, CancellationToken cancel = default)
    {
        var content = view.Analyse(pageIndex);
        var letters = view.LetterDirections(pageIndex);
        var total = letters.Sum();
        // A few typed words on a scan (a stamp, a header added later) say nothing about the scan under them.
        if (total >= (content.HasPictures ? 40 : 12))
        {
            var best = Array.IndexOf(letters, letters.Max());
            var share = (double)letters[best] / total;
            return Decide(best * 90, Math.Clamp((share - 0.5) / 0.4, 0, 1), TurnEvidence.Text, readiness);
        }

        var info = view.Pages[pageIndex];
        var scale = Math.Min(2.5, WorkSize / Math.Max(1, Math.Max(info.Width, info.Height)));
        using var picture = view.Render(pageIndex, scale);
        cancel.ThrowIfCancellationRequested();
        return Look(picture, Codes(languages), read, readiness, (int)Math.Round(72 * scale), cancel);
    }

    /// <summary>As <see cref="Find(PdfView,int,IReadOnlyList{string},bool,double,CancellationToken)"/> for a picture of a page.</summary>
    public static PageTurn Find(SKBitmap image, IReadOnlyList<string>? languages = null, bool read = true, double readiness = UsualReadiness,
        CancellationToken cancel = default)
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
            return Look(source, Codes(languages), read, readiness, 150, cancel);
        }
        finally
        {
            small?.Dispose();
            converted?.Dispose();
        }
    }

    private static List<string> Codes(IReadOnlyList<string>? languages)
    {
        var codes = (languages ?? []).Where(Languages.IsSupported).Select(code => code.ToLowerInvariant()).Distinct().ToList();
        if (codes.Count == 0) codes.Add(Languages.Default);
        return codes;
    }

    /// <summary>
    /// True when the shapes of the letters tell which way up print in these languages stands. They do for Latin
    /// script, where far more rises above the line than hangs below it. In Cyrillic it is the other way about as
    /// often as not, and Japanese signs fill their squares evenly.
    /// </summary>
    private static bool LettersTell(IReadOnlyList<string> languages) => languages.All(code => code is not ("ru" or "ja"));

    /// <summary>
    /// What a finding comes to at another <paramref name="readiness"/> than the one it was made with: the evidence
    /// is the same, only how much of it is asked for before a page is turned changes.
    /// </summary>
    public static PageTurn At(PageTurn found, double readiness) =>
        found.Leaning == 0 ? found : Decide(found.Leaning, found.Certainty, found.Evidence, readiness);

    /// <summary>What is made of the evidence: a turn, no turn, or no turn with a mark to have a look.</summary>
    private static PageTurn Decide(int degrees, double certainty, TurnEvidence evidence, double readiness)
    {
        if (degrees == 0) return new PageTurn(0, certainty, false, evidence);
        var limit = 0.75 - 0.55 * Math.Clamp(readiness, 0, 1);
        if (certainty < limit)
            // It may well be turned, but not enough speaks for it: left as it is, and marked.
            return new PageTurn(0, certainty, true, evidence) { Leaning = degrees };
        // The look of the print alone never makes for certainty: how much rises above a line and hangs below it
        // differs from text to text. A page turned on the strength of it is always one to have a look at.
        return new PageTurn(degrees, certainty, certainty < limit + 0.15 || evidence == TurnEvidence.Look, evidence) { Leaning = degrees };
    }

    // ---- looking at the page as a picture -----------------------------------------------------------------------

    /// <summary>What is printed in a picture: one true value per pixel that is not paper.</summary>
    private sealed record Print(bool[] Dark, int Width, int Height)
    {
        /// <summary>The same print turned clockwise by quarter turns.</summary>
        public Print Turned(int quarters)
        {
            quarters = ((quarters % 4) + 4) % 4;
            if (quarters == 0) return this;
            int w = quarters == 2 ? Width : Height, h = quarters == 2 ? Height : Width;
            var turned = new bool[w * h];
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                if (!Dark[y * Width + x]) continue;
                var (tx, ty) = quarters switch
                {
                    1 => (Height - 1 - y, x),
                    2 => (Width - 1 - x, Height - 1 - y),
                    _ => (y, Width - 1 - x)
                };
                turned[ty * w + tx] = true;
            }

            return new Print(turned, w, h);
        }
    }

    private static unsafe Print? PrintOf(SKBitmap picture)
    {
        int w = picture.Width, h = picture.Height;
        if (w < 40 || h < 40) return null;
        var pixels = (uint*)picture.GetPixels();
        var stride = picture.RowBytes / 4;
        var luma = new byte[w * h];
        var levels = new long[256];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var value = (byte)ImageAnalysis.LumaOf(pixels[y * stride + x]);
            luma[y * w + x] = value;
            levels[value]++;
        }

        // The paper is whatever shade most of the page has.
        var paper = 0;
        long most = -1;
        for (var level = 2; level < 254; level++)
        {
            var around = levels[level - 2] + levels[level - 1] + levels[level] + levels[level + 1] + levels[level + 2];
            if (around > most) (most, paper) = (around, level);
        }

        // How far the darkest and the lightest of the page are from its paper, leaving out the odd pixel.
        long total = (long)w * h, seen = 0;
        int darkest = paper, lightest = paper;
        for (var level = 0; level < 256 && seen < total * 0.003; level++)
        {
            seen += levels[level];
            darkest = Math.Min(paper, level);
        }

        seen = 0;
        for (var level = 255; level >= 0 && seen < total * 0.003; level--)
        {
            seen += levels[level];
            lightest = Math.Max(paper, level);
        }

        // Print is whatever is a third of the way or more from the paper to the far end, on the side where there
        // is more of a far end: dark on light as a rule, light on dark now and then. That takes in the thin strokes
        // a blurred scan has left of its letters, and leaves out the grain of the paper.
        var darkPrint = paper - darkest >= lightest - paper;
        var away = darkPrint ? paper - darkest : lightest - paper;
        if (away < 40) return null;
        var divide = darkPrint ? paper - Math.Max(24, away / 3) : paper + Math.Max(24, away / 3);
        var dark = new bool[w * h];
        long count = 0;
        for (var i = 0; i < dark.Length; i++)
            if (darkPrint ? luma[i] < divide : luma[i] > divide)
            {
                dark[i] = true;
                count++;
            }

        // Next to nothing on it, or more of it covered than print ever covers: nothing to tell lines by.
        return count < dark.Length * 0.002 || count > dark.Length * 0.45 ? null : new Print(dark, w, h);
    }

    /// <summary>
    /// How clearly the print falls into lines that run across (0-1): the share of the printed area that straight
    /// lines from side to side can cross without touching anything. Between lines of text there is such room, row
    /// after row; down through them there is none, some line always has a letter there. A page that is a little
    /// askew is looked at along the slant that shows its lines best.
    /// </summary>
    private static double Room(Print print)
    {
        // Every other pixel is plenty, and keeps this quick.
        var step = Math.Max(1, Math.Max(print.Width, print.Height) / 700);
        int w = print.Width / step, h = print.Height / step;
        var xs = new List<int>();
        var ys = new List<int>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (print.Dark[y * step * print.Width + x * step])
            {
                xs.Add(x);
                ys.Add(y);
            }

        if (xs.Count < 100) return 0;
        // A speck or two does not fill a row.
        var speck = Math.Clamp(xs.Count / 4000, 1, 4);
        var bins = new int[h + 2 * w + 4];
        double best = 0;
        for (var degrees = -5.0; degrees <= 5; degrees += 0.5)
        {
            Array.Clear(bins);
            var tan = Math.Tan(degrees * Math.PI / 180);
            for (var i = 0; i < xs.Count; i++)
            {
                var row = (int)Math.Round(ys[i] - xs[i] * tan) + w;
                if ((uint)row < (uint)bins.Length) bins[row]++;
            }

            int first = Array.FindIndex(bins, count => count > speck), last = Array.FindLastIndex(bins, count => count > speck);
            if (first < 0 || last - first < 24) continue;
            var free = 0;
            for (var row = first; row <= last; row++)
                if (bins[row] <= speck)
                    free++;
            best = Math.Max(best, (double)free / (last - first + 1));
        }

        return best;
    }

    /// <summary>
    /// Which way up lines that run across stand, by their letters (-1 to 1). A line of print has a dense band, the
    /// body of its small letters, with thinner zones above and below it. In Latin script far more rises above the
    /// band (capitals, b d f h k l t) than hangs below it (g j p q y): positive for print that stands upright,
    /// negative for print on its head, near zero where the script or the page gives no such clue.
    /// </summary>
    private static double Upright(Print print)
    {
        const int strips = 10;
        double above = 0, below = 0;
        var width = Math.Max(1, print.Width / strips);
        var rows = new double[print.Height];
        for (var strip = 0; strip < strips; strip++)
        {
            int from = strip * width, to = strip == strips - 1 ? print.Width : from + width;
            double peak = 0;
            for (var y = 0; y < print.Height; y++)
            {
                var count = 0;
                for (var x = from; x < to; x++)
                    if (print.Dark[y * print.Width + x])
                        count++;
                rows[y] = (double)count / (to - from);
                peak = Math.Max(peak, rows[y]);
            }

            if (peak < 0.02) continue;
            for (var y = 0; y < print.Height;)
            {
                // The body of a line: rows at least half as full as lines of this strip get.
                if (rows[y] < peak * 0.35)
                {
                    y++;
                    continue;
                }

                var top = y;
                while (y < print.Height && rows[y] >= peak * 0.35) y++;
                var height = y - top;
                if (height is < 3 or > 80) continue;
                var body = 0.0;
                for (var i = top; i < y; i++) body = Math.Max(body, rows[i]);
                // What sticks out of it, up to its own height away and as long as there is anything at all.
                for (var i = top - 1; i >= Math.Max(0, top - height) && rows[i] > body * 0.02 && rows[i] < body * 0.35; i--) above += rows[i];
                for (var i = y; i < Math.Min(print.Height, y + height) && rows[i] > body * 0.02 && rows[i] < body * 0.35; i++) below += rows[i];
            }
        }

        return above + below < 1e-9 ? 0 : (above - below) / (above + below);
    }

    private static PageTurn Look(SKBitmap picture, IReadOnlyList<string> languages, bool read, double readiness, int dpi, CancellationToken cancel)
    {
        if (PrintOf(picture) is not { } print) return PageTurn.Unknown;

        // Which way the lines run says whether the page lies on its side; the letters, which of the two ways it is.
        double across = Room(print), down = Room(print.Turned(1));
        var standing = across >= 0.12 && across >= down * 2.2;
        var lying = down >= 0.12 && down >= across * 2.2;
        int? guess = null;
        double lead = 0;
        if ((standing || lying) && LettersTell(languages))
        {
            var upright = Upright(lying ? print.Turned(1) : print);
            guess = (lying ? 90 : 0) + (upright >= 0 ? 0 : 180);
            lead = Math.Abs(upright);
        }

        if (read)
            try
            {
                int[] others = standing ? [180] : !lying ? [180, 90, 270] : guess is { } likely ? [likely, (likely + 180) % 360] : [90, 270];
                if (Read(picture, languages, dpi, others, cancel) is { } found)
                    return Decide(found.Degrees, found.Certainty, TurnEvidence.Reading, readiness);
                // Print that reads no way round is print in another language than those chosen, and may be in
                // another script: its letters are no clue then.
                guess = null;
            }
            catch (OcrException)
            {
                // No text recognition here, or not for these languages: the look of the print has to do.
            }

        if (guess is { } turn) return Decide(turn, Math.Min(1, lead / 0.35) * 0.8, TurnEvidence.Look, readiness);
        // On its side for all to see, but which way round cannot be told without reading it: left, and marked.
        return lying ? new PageTurn(0, 0, true, TurnEvidence.Look) : PageTurn.Unknown;
    }

    // ---- reading it each way round ------------------------------------------------------------------------------

    /// <summary>
    /// Reads the page as it stands and, unless that gives proper words, turned the other ways as well, most likely
    /// first. Null when no way of turning it gives anything to go by.
    /// </summary>
    private static (int Degrees, double Certainty)? Read(SKBitmap picture, IReadOnlyList<string> codes, int dpi, int[] others,
        CancellationToken cancel)
    {
        var backend = OcrBackends.Get();
        if (OcrBackends.Missing(backend, codes) is { Count: > 0 } missing) throw new OcrLanguageMissingException(missing);
        var list = string.Join('+', codes.Select(c => Languages.Get(c).TesseractCode));
        // With its light evened out and its speckles gone: a poor scan reads the right way round at all only then.
        var flat = ImageFilters.FlattenBackground(PlanarImage.FromBitmap(picture, true));
        ImageFilters.Denoise(flat);
        using var clean = flat.ToBitmap();

        (int Sure, int All) Words(int degrees)
        {
            cancel.ThrowIfCancellationRequested();
            using var turned = degrees == 0 ? null : Quarter(clean, degrees / 90);
            int sure = 0, all = 0;
            foreach (var word in backend.Recognize(turned ?? clean, list, dpi).SelectMany(line => line.Words))
            {
                var letters = word.Text.Count(char.IsLetterOrDigit);
                if (letters == 0) continue;
                all += letters;
                // The reader also makes out print that runs down the page, and reads it well. A word that stands
                // taller than it is wide was read that way: it says the page lies on its side, not that it is upright.
                if (letters >= 3 && word.Right - word.Left < (word.Bottom - word.Top) * 0.9) continue;
                // Single letters are read into any speck; words are what tells, and the signs of scripts that
                // write a word in one of them.
                if (word.Confidence >= 70 && (letters >= 2 || word.Text.Any(c => c >= 0x3040))) sure += letters;
            }

            return (sure, all);
        }

        var scores = new Dictionary<int, int>();
        var (sure, all) = Words(0);
        scores[0] = sure;
        // Proper words the way it stands: most pages are upright, and need no more than this one look.
        if (sure >= 25 && sure >= all * 0.6) return (0, Math.Min(1, sure / 60.0 + 0.5));

        foreach (var degrees in others.Where(d => d != 0).Distinct())
        {
            (sure, all) = Words(degrees);
            scores[degrees] = sure;
            // Proper words this way, and far more of them than as it stands: the other ways need not be tried.
            if (sure >= 25 && sure >= all * 0.6 && sure >= scores[0] * 3) break;
        }

        // Lines that seemed to run across and read neither way may be a script that is written down the page.
        if (scores.Values.Max() < 8 && others.Length == 1)
            foreach (var degrees in new[] { 90, 270 })
                scores[degrees] = Words(degrees).Sure;

        var ranked = scores.OrderByDescending(score => score.Value).ThenBy(score => score.Key).ToList();
        var best = ranked[0];
        if (best.Value < 8) return null;
        // How far the best way leads the next best, and whether there was enough to read for that to mean much.
        var ahead = (best.Value - ranked[1].Value) / (double)best.Value;
        return (best.Key, ahead * Math.Min(1, best.Value / 30.0));
    }

    /// <summary>A picture turned clockwise by quarter turns.</summary>
    private static SKBitmap Quarter(SKBitmap source, int quarters)
    {
        quarters = ((quarters % 4) + 4) % 4;
        var sideways = quarters % 2 == 1;
        var result = new SKBitmap(new SKImageInfo(sideways ? source.Height : source.Width, sideways ? source.Width : source.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(quarters * 90);
        canvas.DrawBitmap(source, -source.Width / 2f, -source.Height / 2f);
        return result;
    }
}
