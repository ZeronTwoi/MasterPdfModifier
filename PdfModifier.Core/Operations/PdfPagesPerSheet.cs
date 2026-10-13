using PdfModifier.Core.Pdfium;

namespace PdfModifier.Core.Operations;

/// <summary>Which way round the sheets stand that pages are put on.</summary>
public enum SheetTurn
{
    /// <summary>Whichever way leaves the pages larger.</summary>
    Automatic,

    /// <summary>Higher than wide.</summary>
    Upright,

    /// <summary>Wider than high.</summary>
    Sideways
}

/// <summary>
/// How several pages are put on one sheet: the sheet as it is shown (points), how many pages stand across and down,
/// the white around the sheet and between the pages, the order the pages are laid in and whether each gets a thin
/// line around it.
/// </summary>
public sealed record SheetPlan(double Width, double Height, int Columns, int Rows)
{
    public double Margin { get; init; } = 18;
    public double Gap { get; init; } = 9;
    public SheetOrder Order { get; init; } = SheetOrder.Rows;
    public bool Frame { get; init; }

    public int PerSheet => Columns * Rows;

    /// <summary>The room one page has on the sheet.</summary>
    public (double Width, double Height) Cell =>
        (Math.Max(1, (Width - 2 * Margin - (Columns - 1) * Gap) / Columns), Math.Max(1, (Height - 2 * Margin - (Rows - 1) * Gap) / Rows));

    /// <summary>How large a page of the given size comes out on the sheet, as a share of its own size.</summary>
    public double ScaleOf(double pageWidth, double pageHeight)
    {
        var (width, height) = Cell;
        return Math.Min(width / Math.Max(1, pageWidth), height / Math.Max(1, pageHeight));
    }

    /// <summary>Where the page that is the n-th on its sheet (from 0) stands: its column and its row, from the top left.</summary>
    public (int Column, int Row) PlaceOf(int n) => Order switch
    {
        SheetOrder.Columns => (n / Rows, n % Rows),
        SheetOrder.RowsRightToLeft => (Columns - 1 - n % Columns, n / Columns),
        _ => (n % Columns, n / Columns)
    };

    /// <summary>The room of that page on the sheet, in visual coordinates of the sheet.</summary>
    public RectD RoomOf(int n)
    {
        var (column, row) = PlaceOf(n);
        var (width, height) = Cell;
        var left = Margin + column * (width + Gap);
        var top = Margin + row * (height + Gap);
        return new RectD(left, top, left + width, top + height);
    }
}

/// <summary>
/// Puts several pages on each sheet, scaled down: to save paper, to hand out slides, to see a document at a glance.
/// The pages stay what they were, text that can be searched and pictures at their full resolution, only smaller.
/// </summary>
public static unsafe class PdfPagesPerSheet
{
    /// <summary>How many pages stand across and down for a number per sheet: as square as that number allows.</summary>
    public static (int Fewer, int More) Grid(int perSheet)
    {
        perSheet = Math.Max(1, perSheet);
        var fewer = (int)Math.Floor(Math.Sqrt(perSheet));
        while (perSheet % fewer != 0) fewer--;
        return (fewer, perSheet / fewer);
    }

    /// <summary>
    /// Lays out a sheet of the given paper for a number of pages. Turned <see cref="SheetTurn.Automatic"/>, the sheet
    /// stands the way that leaves a page of the given size larger; upright when it makes no difference.
    /// </summary>
    /// <param name="paperWidth">The shorter side of the paper, in points.</param>
    /// <param name="paperHeight">The longer side of the paper, in points.</param>
    public static SheetPlan Plan(int perSheet, double paperWidth, double paperHeight, SheetTurn turn, double pageWidth, double pageHeight,
        double margin = 18, double gap = 9)
    {
        var (fewer, more) = Grid(perSheet);
        double shorter = Math.Min(paperWidth, paperHeight), longer = Math.Max(paperWidth, paperHeight);
        var upright = new SheetPlan(shorter, longer, fewer, more) { Margin = margin, Gap = gap };
        var sideways = new SheetPlan(longer, shorter, more, fewer) { Margin = margin, Gap = gap };
        return turn switch
        {
            SheetTurn.Upright => upright,
            SheetTurn.Sideways => sideways,
            _ => sideways.ScaleOf(pageWidth, pageHeight) > upright.ScaleOf(pageWidth, pageHeight) + 1e-6 ? sideways : upright
        };
    }

    /// <summary>The size of a page as it is shown, in points.</summary>
    public static (double Width, double Height) SizeOf(byte[] pdf, int pageIndex)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            return doc.GetPageSize(Math.Clamp(pageIndex, 0, Math.Max(0, doc.PageCount - 1)));
        }
    }

    /// <summary>How many sheets a number of pages comes to.</summary>
    public static int SheetCount(int pages, SheetPlan plan) => (pages + plan.PerSheet - 1) / plan.PerSheet;

    /// <summary>
    /// Puts the chosen pages on sheets, in their order in the document. The sheets stand where the first of those
    /// pages stood; pages that were not chosen stay as they are. Form fields and comments are made part of the pages
    /// first, so that they show on the sheets as well.
    /// </summary>
    public static byte[] Arrange(byte[] pdf, IReadOnlyCollection<int>? pages, SheetPlan plan, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        if (plan.Columns < 1 || plan.Rows < 1) throw new ArgumentOutOfRangeException(nameof(plan), "A sheet needs at least one page.");
        if (plan.Width < 10 || plan.Height < 10) throw new ArgumentOutOfRangeException(nameof(plan), "The sheet is too small.");

        byte[] flat;
        List<int> chosen;
        lock (PdfDoc.Sync)
        {
            using var counted = PdfDoc.Load(pdf);
            chosen = PdfPages.ResolvePages(pages, counted.PageCount);
        }

        if (chosen.Count == 0) return pdf;
        flat = PdfPages.Flatten(pdf, chosen);
        cancel.ThrowIfCancellationRequested();

        PdfDoc source, target;
        lock (PdfDoc.Sync)
        {
            source = PdfDoc.Load(flat);
            target = PdfDoc.CreateNew();
        }

        try
        {
            lock (PdfDoc.Sync)
            {
                Native.FPDF_CopyViewerPreferences(target.Handle, source.Handle);
                // What stands in front of the first chosen page stays in front of the sheets.
                target.ImportPages(source, Enumerable.Range(0, chosen[0]).ToList(), 0);
            }

            var sheets = SheetCount(chosen.Count, plan);
            for (var sheet = 0; sheet < sheets; sheet++)
            {
                cancel.ThrowIfCancellationRequested();
                // The lock is taken per sheet so the page view can keep drawing during a long job.
                lock (PdfDoc.Sync)
                {
                    using var page = target.NewPage(target.PageCount, plan.Width, plan.Height);
                    for (var n = 0; n < plan.PerSheet && sheet * plan.PerSheet + n < chosen.Count; n++)
                        Place(target, source, page, chosen[sheet * plan.PerSheet + n], plan, n);
                    page.GenerateContent();
                }

                progress?.Report((sheet + 1.0) / sheets * 0.95);
            }

            lock (PdfDoc.Sync)
            {
                // And what was not chosen follows the sheets, in the order it had.
                var rest = Enumerable.Range(chosen[0], source.PageCount - chosen[0]).Except(chosen).ToList();
                target.ImportPages(source, rest, target.PageCount);
                var saved = target.Save();
                progress?.Report(1);
                return saved;
            }
        }
        finally
        {
            lock (PdfDoc.Sync)
            {
                target.Dispose();
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// The sheet a page of the document will be on, as a document of one page, and which sheet of how many that is.
    /// Null for a page that is not among the chosen ones.
    /// </summary>
    public static (byte[] Sheet, int Number, int Of)? SheetWith(byte[] pdf, IReadOnlyCollection<int>? pages, int pageIndex, SheetPlan plan,
        CancellationToken cancel = default)
    {
        List<int> chosen;
        lock (PdfDoc.Sync)
        {
            using var counted = PdfDoc.Load(pdf);
            chosen = PdfPages.ResolvePages(pages, counted.PageCount);
        }

        var at = chosen.IndexOf(pageIndex);
        if (at < 0) return null;
        var sheet = at / plan.PerSheet;
        var onIt = chosen.Skip(sheet * plan.PerSheet).Take(plan.PerSheet).ToList();
        // Made from a document of just those pages: as quick for a book as for a leaflet.
        var few = PdfPages.Extract(pdf, onIt);
        cancel.ThrowIfCancellationRequested();
        return (Arrange(few, null, plan, null, cancel), sheet + 1, SheetCount(chosen.Count, plan));
    }

    private static void Place(PdfDoc target, PdfDoc source, PdfPage sheet, int pageIndex, SheetPlan plan, int n)
    {
        // The page comes over as one object that still holds its text and its pictures. PDFium hands it over the
        // way the page is shown: upright whatever turn the page had, cut to what was visible of it, and with its
        // lower left corner at the origin. (The bounds of the object are those of what is on the page, not of the
        // page, so the page size is what its place is worked out from.)
        var whole = Native.FPDF_NewXObjectFromPage(target.Handle, source.Handle, pageIndex);
        if (whole == 0) throw new PdfException($"Could not take over page {pageIndex + 1}.");
        try
        {
            var obj = Native.FPDF_NewFormObjectFromXObject(whole);
            if (obj == 0) throw new PdfException($"Could not take over page {pageIndex + 1}.");
            var (width, height) = source.GetPageSize(pageIndex);
            var room = plan.RoomOf(n);
            var scale = Math.Min(room.Width / width, room.Height / height);
            // Centred in its room. The sheet is a fresh page: its own coordinates start at the lower left.
            var left = room.Left + (room.Width - width * scale) / 2;
            var bottom = plan.Height - room.Bottom + (room.Height - height * scale) / 2;
            Native.FPDFPageObj_Transform(obj, scale, 0, 0, scale, left, bottom);
            Native.FPDFPage_InsertObject(sheet.Handle, obj);

            if (!plan.Frame) return;
            var frame = Native.FPDFPageObj_CreateNewRect((float)left, (float)bottom, (float)(width * scale), (float)(height * scale));
            if (frame == 0) return;
            Native.FPDFPageObj_SetStrokeColor(frame, 128, 128, 128, 255);
            Native.FPDFPageObj_SetStrokeWidth(frame, 0.5f);
            Native.FPDFPath_SetDrawMode(frame, 0, 1);
            Native.FPDFPage_InsertObject(sheet.Handle, frame);
        }
        finally
        {
            Native.FPDF_CloseXObject(whole);
        }
    }
}
