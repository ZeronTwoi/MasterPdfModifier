using PdfModifier.Core.Fonts;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

/// <summary>
/// An editable stretch of text: neighbouring pieces on one line that share font, size and colour.
/// <see cref="Bounds"/> is in visual page coordinates; <see cref="Id"/> is only valid for the exact document it came from.
/// </summary>
public sealed record TextRun(int Id, string Text, RectD Bounds, double FontSize, string FontName, uint Color)
{
    /// <summary>Where the first letter starts and the line it stands on, in visual page coordinates.</summary>
    public double StartX { get; init; }

    public double Baseline { get; init; }

    /// <summary>An installed family that looks like the run's own font, with its weight and slant.</summary>
    public string Family { get; init; } = "sans-serif";

    public bool Bold { get; init; }
    public bool Italic { get; init; }

    /// <summary>False for text set at an angle or mirrored, which cannot be turned into an upright text box.</summary>
    public bool Upright { get; init; }
}

public abstract record PageEdit;

/// <summary>Changes the text of a run. An empty string deletes it.</summary>
public sealed record ReplaceTextEdit(int RunId, string NewText) : PageEdit;

public sealed record DeleteTextEdit(int RunId) : PageEdit;

/// <summary>Adds new text with its baseline starting at (X, BaselineY) in visual coordinates.</summary>
public sealed record AddTextEdit(double X, double BaselineY, string Text, double FontSize = 12, uint Color = 0xFF000000,
    string? FontFamily = null, bool Bold = false, bool Italic = false) : PageEdit;

public sealed record AddImageEdit(RectD Bounds, byte[] ImageData) : PageEdit;

/// <summary>
/// For scanned pages: paints over an area with its own background colour and types new text on top, matched in size
/// and colour to what was there. Empty text just erases. <see cref="FontSize"/> and <see cref="Color"/> override the estimates.
/// </summary>
public sealed record CoverTextEdit(RectD Bounds, string NewText, double? FontSize = null, uint? Color = null,
    string? FontFamily = null, bool Bold = false) : PageEdit;

public static unsafe class PdfTextEdit
{
    private sealed class Piece
    {
        public nint Object;
        public string Text = "";
        public Affine Matrix;
        public float FontSize;
        public nint Font;
        public uint Color;
        public double Left, Bottom, Right, Top; // page space
        public bool Horizontal;
        public double EffectiveSize => FontSize * Matrix.Scale;
    }

    private sealed class Run
    {
        public readonly List<Piece> Pieces = [];
        public string Text = "";
        public Piece First => Pieces[0];
    }

    public static IReadOnlyList<TextRun> GetTextRuns(byte[] pdf, int pageIndex)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var page = doc.LoadPage(pageIndex);
            var textPage = Native.FPDFText_LoadPage(page.Handle);
            if (textPage == 0) return [];
            try
            {
                var toVisual = page.PageToVisual;
                // A turned page shows upright text sideways; only on an unturned page does "horizontal" mean "reads left to right".
                var unturned = Math.Abs(toVisual.B) < 1e-6 && Math.Abs(toVisual.C) < 1e-6 && toVisual.A > 0;
                return BuildRuns(CollectPieces(page, textPage)).Select((run, id) =>
                {
                    var bounds = run.Pieces
                        .Select(p => toVisual.MapBounds(p.Left, p.Bottom, p.Right, p.Top))
                        .Aggregate((a, b) => a.Union(b));
                    var first = run.First;
                    var (x, baseline) = toVisual.Apply(first.Matrix.E, first.Matrix.F);
                    var match = MatchFont(first.Font);
                    return new TextRun(id, run.Text, bounds, first.EffectiveSize, FontName(first.Font), first.Color)
                    {
                        StartX = x, Baseline = baseline, Family = match.Family, Bold = match.Bold, Italic = match.Italic,
                        Upright = first.Horizontal && unturned
                    };
                }).ToList();
            }
            finally
            {
                Native.FPDFText_ClosePage(textPage);
            }
        }
    }

    /// <summary>Applies a batch of edits to one page. Run ids refer to <see cref="GetTextRuns"/> on the same document.</summary>
    public static byte[] ApplyEdits(byte[] pdf, int pageIndex, IReadOnlyList<PageEdit> edits)
    {
        if (edits.Count == 0) return pdf;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var page = doc.LoadPage(pageIndex);
            using var fonts = new DocFonts(doc);
            // Colours for cover edits are sampled from the page as it looked before any edit of this batch.
            using var snapshot = edits.Any(e => e is CoverTextEdit) ? page.Render(SnapshotScale) : null;

            var textPage = Native.FPDFText_LoadPage(page.Handle);
            try
            {
                var pieces = CollectPieces(page, textPage);
                var runs = BuildRuns(pieces);
                var glyphsInUse = pieces.GroupBy(p => p.Font).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Text).ToHashSet());

                // Announce all new text first so each font is embedded once with every glyph the batch needs.
                var plans = new List<(PageEdit Edit, bool InPlace, (string Family, bool Bold, bool Italic) Font)>();
                foreach (var edit in edits)
                {
                    switch (edit)
                    {
                        case ReplaceTextEdit { NewText.Length: > 0 } replace:
                            var run = RunAt(runs, replace.RunId);
                            // The document's own font is only safe for characters it is already known to contain:
                            // embedded fonts are usually cut down to the glyphs the document uses.
                            var inPlace = replace.NewText.All(c => char.IsWhiteSpace(c) || glyphsInUse[run.First.Font].Contains(c));
                            var match = MatchFont(run.First.Font);
                            if (!inPlace) fonts.Prepare(replace.NewText, match.Family, match.Bold, match.Italic);
                            plans.Add((edit, inPlace, match));
                            break;
                        case AddTextEdit add:
                            fonts.Prepare(add.Text, add.FontFamily, add.Bold, add.Italic);
                            plans.Add((edit, false, default));
                            break;
                        case CoverTextEdit { NewText.Length: > 0 } cover:
                            fonts.Prepare(cover.NewText, cover.FontFamily, cover.Bold);
                            plans.Add((edit, false, default));
                            break;
                        default:
                            plans.Add((edit, false, default));
                            break;
                    }
                }

                var removed = new HashSet<nint>();
                foreach (var (edit, inPlace, font) in plans)
                {
                    switch (edit)
                    {
                        case ReplaceTextEdit { NewText.Length: > 0 } replace:
                            ReplaceRun(page, fonts, pieces, RunAt(runs, replace.RunId), replace.NewText, inPlace, font, removed, ref textPage);
                            break;
                        case ReplaceTextEdit clear:
                            RemoveRun(page, RunAt(runs, clear.RunId), removed);
                            break;
                        case DeleteTextEdit delete:
                            RemoveRun(page, RunAt(runs, delete.RunId), removed);
                            break;
                        case AddTextEdit add when add.Text.Length > 0:
                            PageDraw.DrawText(page, fonts, add.Text,
                                new TextStyle(add.FontSize, add.Color | 0xFF000000, add.FontFamily, add.Bold, add.Italic), add.X, add.BaselineY);
                            break;
                        case AddImageEdit image:
                            using (var bitmap = PdfImages.Decode(image.ImageData))
                                PageDraw.DrawImage(page, bitmap, image.Bounds);
                            break;
                        case CoverTextEdit cover:
                            Cover(page, fonts, pieces, cover, snapshot!, removed);
                            break;
                    }
                }
            }
            finally
            {
                if (textPage != 0) Native.FPDFText_ClosePage(textPage);
            }

            page.GenerateContent();
            return doc.Save();
        }
    }

    private const double SnapshotScale = 2;

    private static Run RunAt(List<Run> runs, int id) =>
        id >= 0 && id < runs.Count ? runs[id] : throw new ArgumentOutOfRangeException(nameof(id), "Unknown text run.");

    private static List<Piece> CollectPieces(PdfPage page, nint textPage)
    {
        var pieces = new List<Piece>();
        var objects = page.GetObjects();
        // A scan laid over text hides it as surely as making it invisible does (some programs store the recognised
        // text that way); changing such text would show nothing.
        double pageArea = page.Width * page.Height;
        var covers = PdfPageAnalysis.Pictures(page, objects)
            .Where(p => p.Index >= 0 && p.Bounds.Width * p.Bounds.Height >= pageArea * PageContent.ScanCoverage)
            .ToList();
        for (var index = 0; index < objects.Count; index++)
        {
            var obj = objects[index];
            if (Native.FPDFPageObj_GetType(obj) != Native.FPDF_PAGEOBJ_TEXT) continue;
            // Invisible text is the layer behind a scan; editing it would change nothing visible.
            if (Native.FPDFTextObj_GetTextRenderMode(obj) == Native.FPDF_TEXTRENDERMODE_INVISIBLE) continue;
            // Text boxes the app added are changed as objects, not line by line.
            if (PdfPageObjects.IsEditable(obj)) continue;
            FS_MATRIX m;
            float size, l, b, r, t;
            if (Native.FPDFPageObj_GetMatrix(obj, &m) == 0 || Native.FPDFTextObj_GetFontSize(obj, &size) == 0) continue;
            if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) continue;
            if (covers.Count > 0)
            {
                var seen = page.PageToVisual.MapBounds(l, b, r, t);
                if (covers.Any(c => c.Index > index && c.Bounds.Contains(seen.CenterX, seen.CenterY))) continue;
            }

            pieces.Add(new Piece
            {
                Object = obj,
                Text = PageDraw.GetObjectText(obj, textPage),
                Matrix = new Affine(m.a, m.b, m.c, m.d, m.e, m.f),
                FontSize = size,
                Font = Native.FPDFTextObj_GetFont(obj),
                Color = PageDraw.GetFill(obj),
                Left = l, Bottom = b, Right = r, Top = t,
                Horizontal = m is { a: > 0, d: > 0 } && Math.Abs(m.b) < 0.01 * m.a && Math.Abs(m.c) < 0.01 * m.d
            });
        }

        return InReadingOrder(pieces);
    }

    /// <summary>
    /// Sorts upright pieces into lines, top to bottom and left to right. Many producers write one piece per word, or
    /// even per letter, and not always in the order the words are read; lines can only be put together when the pieces
    /// that follow each other on the page also follow each other in the list.
    /// </summary>
    private static List<Piece> InReadingOrder(List<Piece> pieces)
    {
        var lines = new List<(double Baseline, double Size, List<Piece> Pieces)>();
        var result = new List<Piece>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (!piece.Horizontal)
            {
                result.Add(piece);
                continue;
            }

            var at = lines.FindIndex(l => Math.Abs(l.Baseline - piece.Matrix.F) < 0.2 * Math.Min(l.Size, piece.EffectiveSize));
            if (at < 0) lines.Add((piece.Matrix.F, piece.EffectiveSize, [piece]));
            else lines[at].Pieces.Add(piece);
        }

        // Page space has y pointing up, so the highest baseline is the first line. The sorts are stable: pieces that
        // start at the same place keep the order the document drew them in.
        result.InsertRange(0, lines.OrderByDescending(l => l.Baseline).SelectMany(l => l.Pieces.OrderBy(p => p.Left)));
        return result;
    }

    /// <summary>Joins pieces that continue each other on one line with the same look. Whitespace-only pieces never start a run.</summary>
    private static List<Run> BuildRuns(List<Piece> pieces)
    {
        var runs = new List<Run>();
        Run? current = null;
        foreach (var piece in pieces)
        {
            var blank = string.IsNullOrWhiteSpace(piece.Text);
            if (current != null && Continues(current.Pieces[^1], piece))
            {
                var previous = current.Pieces[^1];
                var gap = piece.Left - previous.Right;
                var needsSpace = gap > 0.15 * piece.EffectiveSize && current.Text.Length > 0 &&
                                 !char.IsWhiteSpace(current.Text[^1]) && !blank && !char.IsWhiteSpace(piece.Text[0]);
                current.Text += (needsSpace ? " " : "") + piece.Text;
                current.Pieces.Add(piece);
                continue;
            }

            if (blank)
            {
                current = null;
                continue;
            }

            current = new Run { Text = piece.Text };
            current.Pieces.Add(piece);
            runs.Add(current);
        }

        // Leading spaces shift the text and are kept; trailing ones are only an artefact of how the text was split.
        foreach (var run in runs) run.Text = run.Text.TrimEnd();
        return runs;
    }

    private static bool Continues(Piece previous, Piece next)
    {
        if (!previous.Horizontal || !next.Horizontal) return false;
        if (previous.Font != next.Font || previous.Color != next.Color) return false;
        var size = previous.EffectiveSize;
        if (Math.Abs(size - next.EffectiveSize) > 0.02 * size) return false;
        if (Math.Abs(previous.Matrix.F - next.Matrix.F) > 0.2 * size) return false;
        var gap = next.Left - previous.Right;
        return gap > -0.3 * size && gap < 1.0 * size;
    }

    private static void RemoveRun(PdfPage page, Run run, HashSet<nint> removed)
    {
        foreach (var piece in run.Pieces)
            if (removed.Add(piece.Object))
                page.RemoveObject(piece.Object);
    }

    private static void ReplaceRun(PdfPage page, DocFonts fonts, List<Piece> pieces, Run run, string newText, bool inPlace,
        (string Family, bool Bold, bool Italic) font, HashSet<nint> removed, ref nint textPage)
    {
        var first = run.First;
        if (removed.Contains(first.Object)) return;
        var oldRight = run.Pieces.Max(p => p.Right);
        double newRight;

        if (inPlace && SetTextInPlace(page, first, newText, ref textPage))
        {
            foreach (var piece in run.Pieces.Skip(1))
                if (removed.Add(piece.Object))
                    page.RemoveObject(piece.Object);
            newRight = RightOf(first.Object, first.Right);
        }
        else
        {
            // Same text matrix and font size as the original, so the new text sits on the old baseline at the old size.
            var style = new TextStyle(first.FontSize, first.Color, font.Family, font.Bold, font.Italic);
            fonts.Prepare(newText, font.Family, font.Bold, font.Italic);
            var created = PageDraw.DrawTextWithMatrix(page, fonts, newText, style,
                advance => new Affine(1, 0, 0, 1, advance, 0).Then(first.Matrix));
            newRight = created.Count == 0 ? first.Left : created.Max(o => RightOf(o, first.Left));
            RemoveRun(page, run, removed);
        }

        ShiftFollowing(pieces, run, oldRight, newRight - oldRight, removed);
    }

    private static double RightOf(nint obj, double fallback)
    {
        float l, b, r, t;
        return Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 ? r : fallback;
    }

    /// <summary>Rewrites a text object using its own font, then checks the result reads back as intended.</summary>
    private static bool SetTextInPlace(PdfPage page, Piece piece, string newText, ref nint textPage)
    {
        if (Native.FPDFText_SetText(piece.Object, newText) == 0) return false;
        // Fonts with unusual encodings can silently drop or remap characters; reading the page back catches that.
        Native.FPDFText_ClosePage(textPage);
        textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == 0) return false;
        var actual = PageDraw.GetObjectText(piece.Object, textPage);
        return Compact(actual) == Compact(newText);

        static string Compact(string s) => new(s.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
    }

    /// <summary>
    /// Moves the text that directly follows an edited run on the same line, so a longer or shorter replacement does not
    /// collide with it or leave a hole. Stops at the first wide gap, which is how table cells and columns are told apart.
    /// </summary>
    private static void ShiftFollowing(List<Piece> pieces, Run run, double oldRight, double delta, HashSet<nint> removed)
    {
        var first = run.First;
        if (!first.Horizontal || Math.Abs(delta) < 0.01) return;
        var size = first.EffectiveSize;
        var inRun = run.Pieces.Select(p => p.Object).ToHashSet();
        var cursor = oldRight;
        foreach (var piece in pieces
                     .Where(p => p.Horizontal && !inRun.Contains(p.Object) && !removed.Contains(p.Object) &&
                                 Math.Abs(p.Matrix.F - first.Matrix.F) < 0.2 * size && p.Left >= oldRight - 0.3 * size)
                     .OrderBy(p => p.Left))
        {
            if (piece.Left - cursor > 1.5 * size) break;
            Native.FPDFPageObj_Transform(piece.Object, 1, 0, 0, 1, delta, 0);
            cursor = piece.Right;
            piece.Left += delta;
            piece.Right += delta;
            piece.Matrix = piece.Matrix with { E = piece.Matrix.E + delta };
        }
    }

    private static void Cover(PdfPage page, DocFonts fonts, List<Piece> pieces, CoverTextEdit edit, SKBitmap snapshot, HashSet<nint> removed)
    {
        var area = edit.Bounds;
        if (area.Width < 0.5 || area.Height < 0.5) return;
        var (background, ink) = ImageAnalysis.SampleRegionColors(snapshot,
            (int)(area.Left * SnapshotScale), (int)(area.Top * SnapshotScale),
            (int)Math.Ceiling(area.Right * SnapshotScale), (int)Math.Ceiling(area.Bottom * SnapshotScale));

        // Text hidden under the patch would still be found by search and copy, so it goes too.
        var toVisual = page.PageToVisual;
        foreach (var piece in pieces)
        {
            var b = toVisual.MapBounds(piece.Left, piece.Bottom, piece.Right, piece.Top);
            if (b.Left >= area.Left - 1 && b.Right <= area.Right + 1 && b.Top >= area.Top - 1 && b.Bottom <= area.Bottom + 1 &&
                removed.Add(piece.Object))
                page.RemoveObject(piece.Object);
        }

        RemoveHiddenTextInside(page, area, removed);
        PageDraw.DrawRect(page, area, background);
        if (edit.NewText.Length == 0) return;

        // A line box spans roughly ascender to descender; the baseline sits about a fifth above its bottom.
        var size = edit.FontSize ?? Math.Max(3, area.Height * 0.85);
        var width = FontResolver.Measure(edit.NewText, edit.FontFamily, edit.Bold, false, size);
        var stretch = width > area.Width && width > 0 ? Math.Max(0.6, area.Width / width) : 1;
        var baseline = area.Bottom - area.Height * 0.2;
        PageDraw.DrawText(page, fonts, edit.NewText, new TextStyle(size, edit.Color ?? ink, edit.FontFamily, edit.Bold),
            area.Left, baseline, 0, stretch);
    }

    /// <summary>Drops the invisible recognition-layer text lying inside an area.</summary>
    private static void RemoveHiddenTextInside(PdfPage page, RectD area, HashSet<nint> removed)
    {
        foreach (var obj in page.GetObjects())
        {
            if (removed.Contains(obj) || Native.FPDFPageObj_GetType(obj) != Native.FPDF_PAGEOBJ_TEXT) continue;
            if (Native.FPDFTextObj_GetTextRenderMode(obj) != Native.FPDF_TEXTRENDERMODE_INVISIBLE) continue;
            var b = page.GetVisualBounds(obj);
            if (area.Contains(b.CenterX, b.CenterY) && removed.Add(obj)) page.RemoveObject(obj);
        }
    }

    private static string FontName(nint font)
    {
        if (font == 0) return "";
        var buffer = stackalloc byte[256];
        var length = (int)Native.FPDFFont_GetBaseFontName(font, buffer, 256);
        return length is <= 1 or > 256 ? "" : System.Text.Encoding.UTF8.GetString(buffer, length - 1);
    }

    private static (string Family, bool Bold, bool Italic) MatchFont(nint font) =>
        font == 0
            ? ("sans-serif", false, false)
            : FontResolver.ParsePdfFontName(FontName(font), Native.FPDFFont_GetFlags(font), Native.FPDFFont_GetWeight(font));
}
