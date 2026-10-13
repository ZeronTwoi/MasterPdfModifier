using System.Text;
using PdfModifier.Core.Operations;

namespace PdfModifier.Core.Fonts;

/// <summary>One line of a text box. Positions are measured from the box's top-left corner, in points.</summary>
internal sealed record TextLine(string Text, double X, double Baseline, double Width);

/// <summary>
/// Breaks the text of a box into lines and places them. The document writer and the on-screen preview both work from
/// this, so what is shown while editing is what gets written.
/// </summary>
internal sealed class TextLayout
{
    private TextLayout(IReadOnlyList<TextLine> lines, double lineHeight)
    {
        Lines = lines;
        LineHeight = lineHeight;
    }

    public IReadOnlyList<TextLine> Lines { get; }
    public double LineHeight { get; }

    /// <summary>Height the text needs; an empty box still takes one line.</summary>
    public double Height => Math.Max(1, Lines.Count) * LineHeight;

    public static TextLayout Create(PageText text) => Create(text.Text, text.Bounds.Width, text.FontSize, text.FontFamily,
        text.Bold, text.Italic, text.Align, text.LineSpacing);

    public static TextLayout Create(string text, double width, double size, string? family, bool bold, bool italic,
        TextAlign align = TextAlign.Left, double lineSpacing = 1.2)
    {
        var measured = new Dictionary<string, double>();

        double Measure(string piece)
        {
            if (!measured.TryGetValue(piece, out var w)) measured[piece] = w = FontResolver.Measure(piece, family, bold, italic, size);
            return w;
        }

        var primary = FontResolver.Resolve(family, bold, italic)
                      ?? throw new InvalidOperationException("No usable TrueType font is installed on this system.");
        var (ascent, descent) = primary.Metrics(size);
        var lineHeight = size * Math.Max(0.5, lineSpacing);
        // The letters sit in the middle of their line, so the space between lines is shared above and below.
        var firstBaseline = ascent + (lineHeight - (ascent + descent)) / 2;

        var lines = new List<TextLine>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            foreach (var line in Wrap(paragraph, width, Measure))
            {
                var lineWidth = line.Length == 0 ? 0 : Measure(line);
                var x = align switch
                {
                    TextAlign.Center => (width - lineWidth) / 2,
                    TextAlign.Right => width - lineWidth,
                    _ => 0
                };
                lines.Add(new TextLine(line, x, firstBaseline + lines.Count * lineHeight, lineWidth));
            }

        return new TextLayout(lines, lineHeight);
    }

    private static IEnumerable<string> Wrap(string paragraph, double width, Func<string, double> measure)
    {
        if (paragraph.Length == 0 || width <= 0 || double.IsNaN(width))
        {
            yield return paragraph;
            yield break;
        }

        var line = new StringBuilder();
        double lineWidth = 0;
        var wrapped = false;
        foreach (var token in Tokens(paragraph))
        {
            var space = char.IsWhiteSpace(token[0]);
            var tokenWidth = measure(token);
            if (!space && line.Length > 0 && lineWidth + tokenWidth > width + 0.01)
            {
                yield return line.ToString().TrimEnd();
                line.Clear();
                lineWidth = 0;
                wrapped = true;
            }

            if (!space && tokenWidth > width + 0.01)
            {
                // A word wider than the box is cut wherever it has to be.
                foreach (var rune in token.EnumerateRunes())
                {
                    var piece = rune.ToString();
                    var pieceWidth = measure(piece);
                    if (line.Length > 0 && lineWidth + pieceWidth > width + 0.01)
                    {
                        yield return line.ToString();
                        line.Clear();
                        lineWidth = 0;
                        wrapped = true;
                    }

                    line.Append(piece);
                    lineWidth += pieceWidth;
                }

                continue;
            }

            // Spaces a line break swallowed do not indent the next line; spaces the paragraph starts with do.
            if (space && line.Length == 0 && wrapped) continue;
            line.Append(token);
            lineWidth += tokenWidth;
        }

        yield return line.ToString().TrimEnd();
    }

    /// <summary>Words, the gaps between them, and single characters of scripts written without spaces.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var token = new StringBuilder();
        var tokenIsSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var space = Rune.IsWhiteSpace(rune);
            var alone = !space && BreaksAnywhere(rune.Value);
            if (token.Length > 0 && (alone || space != tokenIsSpace))
            {
                yield return token.ToString();
                token.Clear();
            }

            token.Append(rune.ToString());
            tokenIsSpace = space;
            if (alone)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0) yield return token.ToString();
    }

    private static bool BreaksAnywhere(int codePoint) =>
        codePoint is (>= 0x2E80 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF);
}
