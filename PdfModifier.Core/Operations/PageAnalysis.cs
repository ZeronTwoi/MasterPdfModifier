using System.Runtime.InteropServices;
using System.Text;
using PdfModifier.Core.Pdfium;

namespace PdfModifier.Core.Operations;

/// <summary>
/// What a page is made of, as far as it matters for finding and changing its text. A page can be text, a picture of
/// text, or both at once: a scan with a typed header, a scan that already carries a recognised text layer, a document
/// whose content is wrapped in a nested graphic.
/// </summary>
/// <param name="ImageCoverage">Share of the page area (0-1) covered by pictures.</param>
/// <param name="VisibleLetters">Letters and digits of ordinary text that can be edited in place.</param>
/// <param name="HiddenText">Lines of text nobody sees: the search layer over a scan, or text painted over by the scan.</param>
/// <param name="NestedText">Visible lines that are part of a nested graphic and cannot be edited in place.</param>
/// <param name="TextAreas">Where the page shows real text of any kind, line by line, in visual coordinates.</param>
public sealed record PageContent(double ImageCoverage, int VisibleLetters, IReadOnlyList<OcrLine> HiddenText, IReadOnlyList<OcrLine> NestedText,
    IReadOnlyList<RectD> TextAreas)
{
    /// <summary>From this share of pictures on, a page is treated as (also) being a scan.</summary>
    public const double ScanCoverage = 0.25;

    public bool HasPictures => ImageCoverage >= ScanCoverage;
}

internal static unsafe class PdfPageAnalysis
{
    private enum Kind
    {
        Visible,
        Hidden,
        Nested,
        Ignored
    }

    public static PageContent Analyse(PdfPage page)
    {
        var top = page.GetObjects();
        var pictures = Pictures(page, top);
        double pageArea = Math.Max(1, page.Width * page.Height);
        var coverage = Math.Clamp(pictures.Sum(p => p.Bounds.Width * p.Bounds.Height) / pageArea, 0, 1);

        var order = new Dictionary<nint, int>(top.Count);
        for (var i = 0; i < top.Count; i++) order[top[i]] = i;
        // Only large pictures hide text in practice; checking against small ones would just cost time.
        var covers = pictures.Where(p => p.Index >= 0 && p.Bounds.Width * p.Bounds.Height >= pageArea * PageContent.ScanCoverage).ToList();

        var kinds = new Dictionary<nint, Kind>();

        Kind KindOf(nint obj)
        {
            if (kinds.TryGetValue(obj, out var known)) return known;
            Kind kind;
            if (!order.TryGetValue(obj, out var index)) kind = IsInvisible(obj) ? Kind.Hidden : Kind.Nested;
            else if (PdfPageObjects.IsEditable(obj)) kind = Kind.Ignored;
            else if (IsInvisible(obj)) kind = Kind.Hidden;
            else
            {
                var b = page.GetVisualBounds(obj);
                kind = covers.Any(c => c.Index > index && c.Bounds.Contains(b.CenterX, b.CenterY)) ? Kind.Hidden : Kind.Visible;
            }

            return kinds[obj] = kind;
        }

        var hidden = new List<OcrLine>();
        var nested = new List<OcrLine>();
        var areas = new List<RectD>();
        var visibleLetters = 0;
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == 0) return new PageContent(coverage, 0, hidden, nested, areas);
        try
        {
            var toVisual = page.PageToVisual;
            var line = new LineBuilder();
            var lineKind = Kind.Ignored;

            void Flush()
            {
                if (line.Build() is { } built)
                {
                    if (lineKind == Kind.Hidden) hidden.Add(built);
                    else if (lineKind == Kind.Nested) nested.Add(built);
                    if (lineKind != Kind.Hidden) areas.Add(built.Bounds);
                }

                line = new LineBuilder();
            }

            var count = Native.FPDFText_CountChars(textPage);
            for (var i = 0; i < count; i++)
            {
                var code = Native.FPDFText_GetUnicode(textPage, i);
                var obj = Native.FPDFText_GetTextObject(textPage, i);
                if (obj == 0)
                {
                    // Characters the engine made up: a space joins words, a line break ends the line.
                    if (code is '\r' or '\n') Flush();
                    else line.Space();
                    continue;
                }

                var kind = KindOf(obj);
                if (kind != lineKind) Flush();
                lineKind = kind;
                if (kind == Kind.Visible && Rune.IsLetterOrDigit(new Rune(Valid(code)))) visibleLetters++;

                if (code <= ' ' || code == 0xA0)
                {
                    line.Space();
                    continue;
                }

                double l, r, b, t;
                if (Native.FPDFText_GetCharBox(textPage, i, &l, &r, &b, &t) == 0 || r <= l || t <= b) continue;
                var box = toVisual.MapBounds(l, b, r, t);
                if (line.StartsElsewhere(box)) Flush();
                line.Add(char.ConvertFromUtf32((int)Valid(code)), box);
            }

            Flush();
        }
        finally
        {
            Native.FPDFText_ClosePage(textPage);
        }

        return new PageContent(coverage, visibleLetters, hidden, nested, areas);
    }

    private static uint Valid(uint code) => code is >= 0xD800 and <= 0xDFFF or > 0x10FFFF ? 0xFFFD : code;

    private static bool IsInvisible(nint text)
    {
        var mode = Native.FPDFTextObj_GetTextRenderMode(text);
        return mode is Native.FPDF_TEXTRENDERMODE_INVISIBLE or Native.FPDF_TEXTRENDERMODE_CLIP;
    }

    /// <summary>
    /// Pictures on the page with their place as displayed, including those inside nested graphics.
    /// <c>Index</c> is the position among the page's own objects, or -1 for nested ones.
    /// </summary>
    public static List<(RectD Bounds, int Index)> Pictures(PdfPage page, List<nint> top)
    {
        var result = new List<(RectD, int)>();
        var toVisual = page.PageToVisual;
        var pageBox = new RectD(0, 0, page.Width, page.Height);
        for (var i = 0; i < top.Count; i++)
        {
            var type = Native.FPDFPageObj_GetType(top[i]);
            if (type == Native.FPDF_PAGEOBJ_IMAGE)
            {
                if (IsPicture(top[i]) && !PdfPageObjects.IsEditable(top[i]) && Clip(page.GetVisualBounds(top[i]), pageBox) is { } b)
                    result.Add((b, i));
            }
            else if (type == Native.FPDF_PAGEOBJ_FORM)
            {
                Nested(top[i], MatrixOf(top[i]).Then(toVisual), 0);
            }
        }

        return result;

        void Nested(nint form, Affine formToVisual, int depth)
        {
            var count = Native.FPDFFormObj_CountObjects(form);
            for (var i = 0; i < count && result.Count < 5000; i++)
            {
                var obj = Native.FPDFFormObj_GetObject(form, new CULong((uint)i));
                if (obj == 0) continue;
                var type = Native.FPDFPageObj_GetType(obj);
                if (type == Native.FPDF_PAGEOBJ_FORM && depth < 4)
                {
                    Nested(obj, MatrixOf(obj).Then(formToVisual), depth + 1);
                }
                else if (type == Native.FPDF_PAGEOBJ_IMAGE && IsPicture(obj))
                {
                    float l, b, r, t;
                    if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) continue;
                    if (Clip(formToVisual.MapBounds(l, b, r, t), pageBox) is { } box) result.Add((box, -1));
                }
            }
        }
    }

    /// <summary>Tiny images are bullets, rules and textures rather than pictures of anything.</summary>
    private static bool IsPicture(nint image)
    {
        uint w, h;
        return Native.FPDFImageObj_GetImagePixelSize(image, &w, &h) != 0 && w >= 64 && h >= 64;
    }

    private static Affine MatrixOf(nint obj)
    {
        FS_MATRIX m;
        return Native.FPDFPageObj_GetMatrix(obj, &m) != 0 ? new Affine(m.a, m.b, m.c, m.d, m.e, m.f) : Affine.Identity;
    }

    private static RectD? Clip(RectD box, RectD to)
    {
        var clipped = new RectD(Math.Max(box.Left, to.Left), Math.Max(box.Top, to.Top), Math.Min(box.Right, to.Right), Math.Min(box.Bottom, to.Bottom));
        return clipped.Width > 0 && clipped.Height > 0 ? clipped : null;
    }

    /// <summary>Collects the characters of one line and splits them into words.</summary>
    private sealed class LineBuilder
    {
        private readonly List<OcrWord> _words = [];
        private readonly StringBuilder _word = new();
        private RectD _wordBox, _lineBox;
        private bool _any;

        public void Space()
        {
            if (_word.Length == 0) return;
            _words.Add(new OcrWord(_word.ToString(), _wordBox, 100));
            _word.Clear();
        }

        public void Add(string character, RectD box)
        {
            _wordBox = _word.Length == 0 ? box : _wordBox.Union(box);
            _lineBox = _any ? _lineBox.Union(box) : box;
            _any = true;
            _word.Append(character);
        }

        /// <summary>
        /// True when a character cannot belong to the line collected so far: it sits on another level, or far to the
        /// side, as in the next column or table cell.
        /// </summary>
        public bool StartsElsewhere(RectD box)
        {
            if (!_any) return false;
            var height = Math.Max(_lineBox.Height, box.Height);
            if (box.CenterY < _lineBox.Top - 0.3 * height || box.CenterY > _lineBox.Bottom + 0.3 * height) return true;
            return box.Left - _lineBox.Right > 3 * height || _lineBox.Left - box.Right > 3 * height;
        }

        public OcrLine? Build()
        {
            Space();
            if (_words.Count == 0 || !_words.Any(w => w.Text.Any(char.IsLetterOrDigit))) return null;
            // Text set sideways cannot be replaced by an upright line.
            if (_lineBox.Height > 3 * _lineBox.Width && _words.Sum(w => w.Text.Length) >= 3) return null;
            return new OcrLine(string.Join(' ', _words.Select(w => w.Text)), _lineBox, _words.ToList());
        }
    }
}
