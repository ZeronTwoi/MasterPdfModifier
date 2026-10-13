using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace PdfModifier.Core.Pdfium;

public class PdfException(string message) : Exception(message);

/// <summary>Thrown when a document needs a password, or the supplied one is wrong.</summary>
public sealed class PdfPasswordException() : PdfException("The document is password protected.");

/// <summary>Owns a PDFium document handle. PDFium is not thread-safe: hold <see cref="Sync"/> for every use.</summary>
internal sealed unsafe class PdfDoc : IDisposable
{
    public static readonly object Sync = new();

    private GCHandle _pinned;

    static PdfDoc() => Native.FPDF_InitLibrary();

    private PdfDoc(nint handle, GCHandle pinned)
    {
        Handle = handle;
        _pinned = pinned;
    }

    public nint Handle { get; private set; }

    public int PageCount => Native.FPDF_GetPageCount(Handle);

    public static PdfDoc Load(byte[] pdf, string? password = null)
    {
        // PDFium reads lazily from the buffer, so it has to stay pinned for the document's lifetime.
        var pinned = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        var handle = Native.FPDF_LoadMemDocument64((byte*)pinned.AddrOfPinnedObject(), (nuint)pdf.Length, password);
        if (handle != 0) return new PdfDoc(handle, pinned);

        var error = (int)Native.FPDF_GetLastError().Value;
        pinned.Free();
        throw error == Native.FPDF_ERR_PASSWORD
            ? new PdfPasswordException()
            : new PdfException("The file is not a valid PDF document or is damaged.");
    }

    public static PdfDoc CreateNew()
    {
        var handle = Native.FPDF_CreateNewDocument();
        if (handle == 0) throw new PdfException("Could not create a new document.");
        return new PdfDoc(handle, default);
    }

    public PdfPage LoadPage(int index)
    {
        var page = Native.FPDF_LoadPage(Handle, index);
        if (page == 0) throw new PdfException($"Could not load page {index + 1}.");
        return new PdfPage(this, page, index);
    }

    public PdfPage NewPage(int index, double width, double height)
    {
        var page = Native.FPDFPage_New(Handle, index, width, height);
        if (page == 0) throw new PdfException("Could not create a page.");
        return new PdfPage(this, page, index);
    }

    public (double Width, double Height) GetPageSize(int index)
    {
        FS_SIZEF size;
        if (Native.FPDF_GetPageSizeByIndexF(Handle, index, &size) == 0) return (612, 792);
        return (size.width, size.height);
    }

    public void ImportPages(PdfDoc source, IReadOnlyList<int>? pages, int insertAt)
    {
        var indices = (pages ?? Enumerable.Range(0, source.PageCount).ToArray()).ToArray();
        if (indices.Length == 0) return;
        fixed (int* p = indices)
        {
            if (Native.FPDF_ImportPagesByIndex(Handle, source.Handle, p, new CULong((uint)indices.Length), insertAt) == 0)
                throw new PdfException("Could not import pages.");
        }
    }

    public string GetMetaText(string tag)
    {
        var bytes = (int)Native.FPDF_GetMetaText(Handle, tag, null, new CULong(0)).Value;
        if (bytes <= 2) return "";
        var buffer = new byte[bytes];
        fixed (byte* p = buffer) Native.FPDF_GetMetaText(Handle, tag, p, new CULong((uint)bytes));
        return Encoding.Unicode.GetString(buffer, 0, bytes - 2);
    }

    public byte[] Save(bool removeSecurity = false)
    {
        var stream = new MemoryStream();
        var target = GCHandle.Alloc(stream);
        try
        {
            var writer = new FPDF_FILEWRITE { version = 1, WriteBlock = &WriteBlock, target = GCHandle.ToIntPtr(target) };
            var flags = removeSecurity ? Native.FPDF_REMOVE_SECURITY : Native.FPDF_NO_INCREMENTAL;
            if (Native.FPDF_SaveAsCopy(Handle, &writer, new CULong(flags)) == 0)
                throw new PdfException("Could not save the document.");
        }
        finally
        {
            target.Free();
        }

        return stream.ToArray();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, CULong size)
    {
        var stream = (MemoryStream)GCHandle.FromIntPtr(self->target).Target!;
        stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
        return 1;
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            Native.FPDF_CloseDocument(Handle);
            Handle = 0;
        }

        if (_pinned.IsAllocated) _pinned.Free();
    }
}

internal sealed unsafe class PdfPage(PdfDoc doc, nint handle, int index) : IDisposable
{
    private Affine? _visualToPage;

    public PdfDoc Doc { get; } = doc;
    public nint Handle { get; private set; } = handle;
    public int Index { get; } = index;

    /// <summary>Size in points as displayed, i.e. after the page rotation is applied.</summary>
    public double Width => Native.FPDF_GetPageWidthF(Handle);

    public double Height => Native.FPDF_GetPageHeightF(Handle);

    /// <summary>Rotation in quarter turns clockwise (0-3).</summary>
    public int Rotation
    {
        get => Native.FPDFPage_GetRotation(Handle);
        set => Native.FPDFPage_SetRotation(Handle, ((value % 4) + 4) % 4);
    }

    /// <summary>Maps visual coordinates (points, origin top-left, y down) to PDF user space.</summary>
    public Affine VisualToPage
    {
        get
        {
            if (_visualToPage is { } cached) return cached;
            const int n = 10000;
            double x0, y0, x1, y1, x2, y2;
            Native.FPDF_DeviceToPage(Handle, 0, 0, n, n, 0, 0, 0, &x0, &y0);
            Native.FPDF_DeviceToPage(Handle, 0, 0, n, n, 0, n, 0, &x1, &y1);
            Native.FPDF_DeviceToPage(Handle, 0, 0, n, n, 0, 0, n, &x2, &y2);
            double w = Width, h = Height;
            var m = new Affine((x1 - x0) / w, (y1 - y0) / w, (x2 - x0) / h, (y2 - y0) / h, x0, y0);
            _visualToPage = m;
            return m;
        }
    }

    public Affine PageToVisual => VisualToPage.Invert();

    public int ObjectCount => Native.FPDFPage_CountObjects(Handle);

    public List<nint> GetObjects()
    {
        var count = ObjectCount;
        var list = new List<nint>(count);
        for (var i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(Handle, i);
            if (obj != 0) list.Add(obj);
        }

        return list;
    }

    public void RemoveObject(nint obj)
    {
        if (Native.FPDFPage_RemoveObject(Handle, obj) != 0) Native.FPDFPageObj_Destroy(obj);
    }

    public void GenerateContent()
    {
        if (Native.FPDFPage_GenerateContent(Handle) == 0)
            throw new PdfException($"Could not write the content of page {Index + 1}.");
    }

    /// <summary>Bounds of a page object in visual coordinates.</summary>
    public RectD GetVisualBounds(nint obj)
    {
        float l, b, r, t;
        if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) return default;
        return PageToVisual.MapBounds(l, b, r, t);
    }

    /// <summary>The visible box in user space (crop box when present, media box otherwise).</summary>
    public (double Left, double Bottom, double Right, double Top) GetUserBox()
    {
        float l, b, r, t;
        if (Native.FPDFPage_GetCropBox(Handle, &l, &b, &r, &t) != 0 && r > l && t > b) return (l, b, r, t);
        if (Native.FPDFPage_GetMediaBox(Handle, &l, &b, &r, &t) != 0 && r > l && t > b) return (l, b, r, t);
        return Rotation % 2 == 0 ? (0, 0, Width, Height) : (0, 0, Height, Width);
    }

    /// <summary>
    /// Renders the page at <paramref name="scale"/> pixels per point. Objects for which <paramref name="hide"/> answers
    /// true are left out of the picture; the page itself is not changed.
    /// </summary>
    public SKBitmap Render(double scale, bool annotations = true, Func<nint, bool>? hide = null) =>
        Render(new RectD(0, 0, Width, Height), scale, annotations, hide);

    /// <summary>
    /// Renders the part of the page inside <paramref name="area"/> (visual coordinates) at <paramref name="scale"/>
    /// pixels per point. A small part can be drawn at a resolution the whole page would be far too large for.
    /// </summary>
    public SKBitmap Render(RectD area, double scale, bool annotations = true, Func<nint, bool>? hide = null)
    {
        var w = Math.Max(1, (int)Math.Round(area.Width * scale));
        var h = Math.Max(1, (int)Math.Round(area.Height * scale));
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var target = Native.FPDFBitmap_CreateEx(w, h, Native.FPDFBitmap_BGRA, (void*)bitmap.GetPixels(), bitmap.RowBytes);
        if (target == 0)
        {
            bitmap.Dispose();
            throw new PdfException("Could not allocate the render target.");
        }

        var hidden = hide == null ? [] : GetObjects().Where(hide).ToList();
        try
        {
            foreach (var obj in hidden) Native.FPDFPageObj_SetIsActive(obj, 0);
            Native.FPDFBitmap_FillRect(target, 0, 0, w, h, new CULong(0xFFFFFFFF));
            // The whole page is laid out at the scale asked for and shifted so that the area lands on the picture.
            Native.FPDF_RenderPageBitmap(target, Handle, -(int)Math.Round(area.Left * scale), -(int)Math.Round(area.Top * scale),
                Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)), 0, annotations ? Native.FPDF_ANNOT : 0);
        }
        finally
        {
            foreach (var obj in hidden) Native.FPDFPageObj_SetIsActive(obj, 1);
            Native.FPDFBitmap_Destroy(target);
        }

        return bitmap;
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Native.FPDF_ClosePage(Handle);
        Handle = 0;
    }
}
