using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

/// <summary>A document kept open for display: page sizes, rendering and text extraction without re-parsing each time.</summary>
public sealed class PdfView : IDisposable
{
    private readonly PdfDoc _doc;
    private bool _disposed;

    public PdfView(byte[] pdf)
    {
        lock (PdfDoc.Sync)
        {
            _doc = PdfDoc.Load(pdf);
            PageCount = _doc.PageCount;
            var pages = new PageInfo[PageCount];
            for (var i = 0; i < pages.Length; i++)
            {
                // Cheap: reads the page dictionary only, without parsing the content.
                var (w, h) = _doc.GetPageSize(i);
                pages[i] = new PageInfo(w, h, 0);
            }

            Pages = pages;
        }
    }

    public int PageCount { get; }

    /// <summary>Page sizes as displayed, in points.</summary>
    public IReadOnlyList<PageInfo> Pages { get; }

    /// <summary>
    /// Renders a page at <paramref name="scale"/> pixels per point into a BGRA bitmap. The caller owns the result.
    /// With <paramref name="withoutEditable"/> the objects the app added (see <see cref="PdfPageObjects"/>) are left out,
    /// which is the page as it was before them.
    /// </summary>
    public SKBitmap Render(int pageIndex, double scale, bool withoutEditable = false)
    {
        lock (PdfDoc.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _doc.LoadPage(pageIndex);
            return page.Render(scale, hide: withoutEditable ? PdfPageObjects.IsEditable : null);
        }
    }

    /// <summary>As <see cref="Render(int,double,bool)"/> for the part of the page inside <paramref name="area"/> (visual coordinates).</summary>
    public SKBitmap RenderArea(int pageIndex, RectD area, double scale, bool withoutEditable = false)
    {
        lock (PdfDoc.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _doc.LoadPage(pageIndex);
            return page.Render(area, scale, hide: withoutEditable ? PdfPageObjects.IsEditable : null);
        }
    }

    /// <summary>Tells how much of a page is pictures and finds the text that cannot be edited in place.</summary>
    public PageContent Analyse(int pageIndex)
    {
        lock (PdfDoc.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _doc.LoadPage(pageIndex);
            return PdfPageAnalysis.Analyse(page);
        }
    }

    /// <summary>
    /// How many letters of the page's text stand each way, counted by the turn that would set them upright: [0]
    /// those that are upright as the page is shown, [1] those that a quarter turn clockwise would set upright, and
    /// so on. Letters that stand at a slant (a watermark across the page) are left out.
    /// </summary>
    internal int[] LetterDirections(int pageIndex)
    {
        lock (PdfDoc.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _doc.LoadPage(pageIndex);
            var counts = new int[4];
            var textPage = Native.FPDFText_LoadPage(page.Handle);
            if (textPage == 0) return counts;
            try
            {
                var rotation = page.Rotation;
                var letters = Native.FPDFText_CountChars(textPage);
                for (var i = 0; i < letters; i++)
                {
                    var code = Native.FPDFText_GetUnicode(textPage, i);
                    if (code > 0xFFFF || !char.IsLetterOrDigit((char)code)) continue;
                    var angle = Native.FPDFText_GetCharAngle(textPage, i);
                    if (angle < 0) continue;
                    // How far the letter is turned clockwise as the page is shown; undoing that sets it upright.
                    var clockwise = angle * 180 / Math.PI + rotation * 90;
                    var quarter = Math.Round(clockwise / 90);
                    if (Math.Abs(clockwise - quarter * 90) > 20) continue;
                    counts[((4 - (int)quarter) % 4 + 4) % 4]++;
                }

                return counts;
            }
            finally
            {
                Native.FPDFText_ClosePage(textPage);
            }
        }
    }

    public string GetText(int pageIndex)
    {
        lock (PdfDoc.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _doc.LoadPage(pageIndex);
            var textPage = Native.FPDFText_LoadPage(page.Handle);
            if (textPage == 0) return "";
            try
            {
                return PageDraw.GetPageText(textPage);
            }
            finally
            {
                Native.FPDFText_ClosePage(textPage);
            }
        }
    }

    public void Dispose()
    {
        lock (PdfDoc.Sync)
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Dispose();
        }
    }
}
