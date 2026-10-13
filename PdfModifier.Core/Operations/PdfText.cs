using System.Text;
using PdfModifier.Core.Pdfium;

namespace PdfModifier.Core.Operations;

/// <summary>How a phrase is looked for.</summary>
public sealed record FindOptions(bool MatchCase = false, bool WholeWords = false);

/// <summary>
/// A place where a phrase was found: the page, where on it (one area per line the phrase runs over, in visual
/// coordinates), the words around it, whether any of it was read off a picture rather than taken from the page's
/// text, and whether that reading was a close call.
/// </summary>
public sealed record TextHit(int Page, IReadOnlyList<RectD> Areas, string Before, string Found, string After, bool Read, bool Doubtful)
{
    /// <summary>The hit in the words around it, as one line.</summary>
    public string InContext => Before + Found + After;
}

/// <summary>What a page says, line by line, and whether part of that was read off a picture rather than taken from the page's text.</summary>
public sealed record PageWords(IReadOnlyList<OcrLine> Lines, bool Read);

/// <summary>A line of a page's own text with the print it is set in: the size of its letters in points, and whether they are bold.</summary>
public sealed record PrintedLine(OcrLine Line, double Size, bool Bold);

/// <summary>
/// The text of a page with where every word stands: for finding a phrase, and for taking text out of a page or a
/// part of it. A page's own text is taken as it is, whether it shows, lies in a nested graphic or is the search
/// layer over a scan; where a page is only a picture of text, the text is read off the picture.
/// </summary>
public static unsafe class PdfText
{
    private const int Around = 36;

    /// <summary>
    /// What a page says. With <paramref name="reading"/>, a page that is a picture of text (or partly one) is read;
    /// without it such a page gives only the text it has as text, which may be none.
    /// </summary>
    public static PageWords Of(PdfView view, int pageIndex, OcrSettings? reading = null, CancellationToken cancel = default)
    {
        var typed = view.GetLines(pageIndex);
        if (reading == null || !Kind(view, pageIndex).WorthReading) return new PageWords(typed, false);
        var read = PdfOcr.RecognizePage(view, pageIndex, reading, cancel).Lines;
        // What the page has as text is taken as it is; reading adds what is there only as a picture.
        var added = read.Where(line => !typed.Any(known => Covers(known.Bounds, line.Bounds))).ToList();
        if (added.Count == 0) return new PageWords(typed, false);
        return new PageWords(typed.Concat(added).OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToList(), true);
    }

    /// <summary>
    /// What a page is, for taking its text. Worth reading: part of what it says may be there only as a picture,
    /// which also goes for a page with hardly a letter on it (letters can be drawn as shapes). A scan: it has a
    /// picture large enough to be the page, and no text to go with it. Reading is tried on the first kind; only of
    /// the second can it be said that something was passed over when it was not read.
    /// </summary>
    public static (bool WorthReading, bool Scan) Kind(PdfView view, int pageIndex)
    {
        var content = view.Analyse(pageIndex);
        var worth = PdfOcr.NeedsReading(content);
        return (worth, worth && content.HasPictures);
    }

    /// <summary>True for a page that is a scan: its text is there only as a picture, and has to be read to be had.</summary>
    public static bool IsPicture(PdfView view, int pageIndex) => Kind(view, pageIndex).Scan;

    /// <summary>The lines of text a page carries as text, in the order the page has them.</summary>
    internal static List<OcrLine> Lines(PdfPage page) => PrintedLines(page).Select(printed => printed.Line).ToList();

    /// <summary>The same lines with the print each is set in.</summary>
    internal static List<PrintedLine> PrintedLines(PdfPage page)
    {
        var lines = new List<PrintedLine>();
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == 0) return lines;
        try
        {
            var toVisual = page.PageToVisual;
            var turned = page.Rotation * Math.PI / 2;
            var words = new List<OcrWord>();
            var word = new StringBuilder();
            RectD wordBox = default, lineBox = default;
            bool wordHasBox = false, lineHasBox = false;
            // The print of the letters of the line in hand: how many are of which size, and how many are bold.
            var sizes = new Dictionary<double, int>();
            int letters = 0, bold = 0;
            var prints = new Dictionary<nint, (double Size, bool Bold)>();

            (double Size, bool Bold) PrintOf(nint obj)
            {
                if (prints.TryGetValue(obj, out var known)) return known;
                // The size the page sets, times what its placing on the page makes of it: many programs set every
                // font at size 1 and say the real size there.
                float size;
                FS_MATRIX m;
                double points = Native.FPDFTextObj_GetFontSize(obj, &size) != 0 ? size : 0;
                if (Native.FPDFPageObj_GetMatrix(obj, &m) != 0) points *= Math.Sqrt(Math.Abs((double)m.a * m.d - (double)m.b * m.c));
                var font = Native.FPDFTextObj_GetFont(obj);
                return prints[obj] = (Math.Round(points * 2) / 2, font != 0 && Native.FPDFFont_GetWeight(font) >= 600);
            }

            void EndWord()
            {
                if (word.Length > 0 && wordHasBox) words.Add(new OcrWord(word.ToString(), wordBox, 100));
                word.Clear();
                wordHasBox = false;
            }

            void EndLine()
            {
                EndWord();
                if (words.Count > 0)
                {
                    // Where the page does not say a size, the height of the line has to do.
                    var size = sizes.Count > 0 && sizes.MaxBy(pair => pair.Value).Key is > 0 and var most ? most : Math.Round(lineBox.Height * 2) / 2;
                    lines.Add(new PrintedLine(new OcrLine(Join(words), lineBox, words.ToList()), size, letters > 0 && bold >= 0.9 * letters));
                }

                words.Clear();
                sizes.Clear();
                letters = bold = 0;
                lineHasBox = false;
            }

            var count = Native.FPDFText_CountChars(textPage);
            for (var i = 0; i < count; i++)
            {
                var code = Native.FPDFText_GetUnicode(textPage, i);
                var obj = Native.FPDFText_GetTextObject(textPage, i);
                if (obj == 0)
                {
                    // Characters the engine made up: a space ends the word, a line break the line.
                    if (code is '\r' or '\n') EndLine();
                    else EndWord();
                    continue;
                }

                if (code <= ' ' || code == 0xA0)
                {
                    EndWord();
                    continue;
                }

                if (code is >= 0xD800 and <= 0xDFFF or > 0x10FFFF or 0xFFFE or 0xFFFF) continue;
                double l, r, b, t;
                var boxed = Native.FPDFText_GetCharBox(textPage, i, &l, &r, &b, &t) != 0 && r > l && t > b;
                if (boxed)
                {
                    var box = toVisual.MapBounds(l, b, r, t);
                    // Text that runs across the page keeps to its level; what stands on another, or far off to the
                    // side as in the next column, is a line of its own. Text set at an angle is left to the engine.
                    var angle = Native.FPDFText_GetCharAngle(textPage, i);
                    var across = angle < 0 || Math.Abs(Math.Sin(angle + turned)) < 0.35;
                    if (lineHasBox && across && StartsElsewhere(lineBox, box)) EndLine();
                    wordBox = wordHasBox ? wordBox.Union(box) : box;
                    lineBox = lineHasBox ? lineBox.Union(box) : box;
                    wordHasBox = lineHasBox = true;
                }

                var print = PrintOf(obj);
                sizes[print.Size] = sizes.GetValueOrDefault(print.Size) + 1;
                letters++;
                if (print.Bold) bold++;
                word.Append(char.ConvertFromUtf32((int)code));
            }

            EndLine();
        }
        finally
        {
            Native.FPDFText_ClosePage(textPage);
        }

        return lines;
    }

    private static bool StartsElsewhere(RectD line, RectD box)
    {
        var height = Math.Max(line.Height, box.Height);
        if (box.CenterY < line.Top - 0.3 * height || box.CenterY > line.Bottom + 0.3 * height) return true;
        return box.Left - line.Right > 3 * height || line.Left - box.Right > 3 * height;
    }

    private static bool Covers(RectD known, RectD line) =>
        known.Contains(line.CenterX, line.CenterY) || line.Contains(known.CenterX, known.CenterY);

    // ---- finding ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds a phrase in the lines of a page. Capitals count only when asked for; a phrase may run over the end of a
    /// line, also where a word was divided there; and letters written as one sign (ﬁ) are found by their letters.
    /// </summary>
    public static IReadOnlyList<TextHit> Find(IReadOnlyList<OcrLine> lines, int page, string phrase, FindOptions? options = null)
    {
        options ??= new FindOptions();
        var wanted = string.Join(' ', Plain(phrase).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var hits = new List<TextHit>();
        if (wanted.Length == 0) return hits;

        // All words of the page in one row, with a note of which word each stretch of it is.
        var text = new StringBuilder();
        var parts = new List<(int Start, int Length, int Line, int Word)>();
        for (var l = 0; l < lines.Count; l++)
        for (var w = 0; w < lines[l].Words.Count; w++)
        {
            var word = Plain(lines[l].Words[w].Text);
            if (word.Length == 0) continue;
            if (parts.Count > 0)
            {
                var last = parts[^1];
                // A word divided at the end of a line is one word: "exam-" and "ple" are found as "example".
                var divided = w == 0 && last.Line != l && last.Length > 1 && text[^1] is '-' or '‐' or '­' &&
                              char.IsLetter(text[^2]) && char.IsLower(word[0]);
                if (divided)
                {
                    text.Length--;
                    parts[^1] = last with { Length = last.Length - 1 };
                }
                else if (!(IsCjk(text[^1]) && IsCjk(word[0])))
                {
                    text.Append(' ');
                }
            }

            parts.Add((text.Length, word.Length, l, w));
            text.Append(word);
        }

        var shown = text.ToString();
        var haystack = Fold(shown, options.MatchCase);
        var needle = Fold(wanted, options.MatchCase);
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;)
        {
            var end = at + needle.Length;
            if (options.WholeWords && ((at > 0 && char.IsLetterOrDigit(haystack[at - 1])) || (end < haystack.Length && char.IsLetterOrDigit(haystack[end]))))
            {
                at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal);
                continue;
            }

            var areas = new SortedDictionary<int, RectD>();
            bool read = false, doubtful = false;
            foreach (var part in parts)
            {
                if (part.Start + part.Length <= at || part.Start >= end) continue;
                var word = lines[part.Line].Words[part.Word];
                // Words of the page's own text are certain; what was read off a picture comes with how sure the reading was.
                read |= word.Confidence < 100;
                doubtful |= word.Confidence < PdfOcr.DoubtfulBelow;
                var box = word.Bounds;
                // Of a word that is only partly meant, the share of it that is: letters are taken to be of one width.
                if (part.Length > 0 && box.Width >= box.Height)
                {
                    double from = Math.Max(at, part.Start) - part.Start, to = Math.Min(end, part.Start + part.Length) - part.Start;
                    box = new RectD(box.Left + box.Width * from / part.Length, box.Top, box.Left + box.Width * to / part.Length, box.Bottom);
                }

                areas[part.Line] = areas.TryGetValue(part.Line, out var known) ? known.Union(box) : box;
            }

            int before = Math.Max(0, at - Around), after = Math.Min(shown.Length, end + Around);
            hits.Add(new TextHit(page, areas.Values.ToList(), (before > 0 ? "…" : "") + shown[before..at], shown[at..end],
                shown[end..after] + (after < shown.Length ? "…" : ""), read, doubtful));
            at = haystack.IndexOf(needle, end, StringComparison.Ordinal);
        }

        return hits;
    }

    /// <summary>Letters as they are written out: signs that stand for several letters, and wide and narrow forms, become the plain ones.</summary>
    private static string Plain(string text) => text.Normalize(NormalizationForm.FormKC);

    /// <summary>
    /// Text made comparable, letter for letter: without capitals unless they count, the two letters i of Turkish
    /// taken for the one of everyone else, and the typographic quotes for the plain ones.
    /// </summary>
    private static string Fold(string text, bool matchCase) => string.Create(text.Length, (text, matchCase), static (span, state) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            var c = state.text[i];
            span[i] = c switch
            {
                '’' or '‘' or 'ʼ' => '\'',
                '“' or '”' or '„' => '"',
                ' ' => ' ',
                _ when state.matchCase => c,
                'İ' or 'ı' => 'I',
                _ => char.ToUpperInvariant(c)
            };
        }
    });

    // ---- taking text out ----------------------------------------------------------------------------------------

    /// <summary>
    /// The text of a page, or of the part of it inside <paramref name="area"/> (visual coordinates), line by line.
    /// A wider gap between two lines is kept as an empty line, the way paragraphs are set apart.
    /// </summary>
    public static string In(IReadOnlyList<OcrLine> lines, RectD? area = null)
    {
        var text = new StringBuilder();
        RectD? previous = null;
        foreach (var line in lines)
        {
            var words = area is { } within ? line.Words.Where(word => within.Contains(word.Bounds.CenterX, word.Bounds.CenterY)).ToList() : line.Words;
            if (words.Count == 0) continue;
            var box = words.Select(word => word.Bounds).Aggregate((a, b) => a.Union(b));
            if (previous is { } above)
            {
                text.Append('\n');
                if (box.Top - above.Bottom > 0.9 * Math.Max(1, Math.Min(box.Height, above.Height)) && box.Top > above.Top) text.Append('\n');
            }

            text.Append(Join(words));
            previous = box;
        }

        return text.ToString();
    }

    /// <summary>Joins words with spaces, except between Chinese and Japanese characters, which are written without them.</summary>
    private static string Join(IReadOnlyList<OcrWord> words)
    {
        var builder = new StringBuilder(words[0].Text);
        for (var i = 1; i < words.Count; i++)
        {
            if (!(IsCjk(words[i - 1].Text[^1]) && IsCjk(words[i].Text[0]))) builder.Append(' ');
            builder.Append(words[i].Text);
        }

        return builder.ToString();
    }

    private static bool IsCjk(char c) => c is >= '　' and <= '鿿' or >= '＀' and <= '￯';
}
