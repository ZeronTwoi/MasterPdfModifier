using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using PdfModifier.Core.Fonts;
using SkiaSharp;

namespace PdfModifier.Core.Pdfium;

/// <summary>
/// Embeds fonts into one document. Text must be announced with <see cref="Prepare"/> before the first draw so each font
/// is embedded once, reduced to the glyphs the whole operation needs.
/// </summary>
internal sealed unsafe class DocFonts(PdfDoc doc) : IDisposable
{
    private readonly Dictionary<ResolvedFont, HashSet<int>> _planned = new();
    private readonly Dictionary<ResolvedFont, (nint Handle, HashSet<int> Covered)> _loaded = new();
    private readonly List<nint> _handles = [];

    public void Prepare(string text, string? family, bool bold = false, bool italic = false)
    {
        foreach (var (run, font) in FontResolver.Itemize(text, family, bold, italic))
        {
            if (!_planned.TryGetValue(font, out var set)) _planned[font] = set = [];
            foreach (var rune in run.EnumerateRunes()) set.Add(rune.Value);
        }
    }

    /// <summary>
    /// Announces every character of a whole job at once. Batches that announce the same set embed byte-identical fonts,
    /// which are folded into a single copy when the batches are put back together.
    /// </summary>
    public void PrepareAll(IEnumerable<string> texts, string? family, bool bold = false, bool italic = false)
    {
        var runes = new SortedSet<int>();
        foreach (var text in texts)
            foreach (var rune in text.EnumerateRunes())
                runes.Add(rune.Value);
        Prepare(string.Concat(runes.Select(char.ConvertFromUtf32)), family, bold, italic);
    }

    public nint Get(ResolvedFont font, string text)
    {
        var needed = text.EnumerateRunes().Select(r => r.Value).ToList();
        if (_loaded.TryGetValue(font, out var loaded) && needed.All(loaded.Covered.Contains)) return loaded.Handle;

        // First use, or text that was not announced: embed a (new) subset covering everything known so far.
        if (!_planned.TryGetValue(font, out var planned)) _planned[font] = planned = [];
        planned.UnionWith(needed);
        var data = FontSubsetter.Subset(font.Data, planned.ToDictionary(cp => cp, cp => font.Typeface.GetGlyph(cp)));
        nint handle;
        fixed (byte* p = data)
            handle = Native.FPDFText_LoadFont(doc.Handle, p, (uint)data.Length, Native.FPDF_FONT_TRUETYPE, 1);
        if (handle == 0) throw new PdfException($"Could not embed the font '{font.Family}'.");
        _handles.Add(handle);
        _loaded[font] = (handle, [..planned]);
        return handle;
    }

    public void Dispose()
    {
        foreach (var handle in _handles) Native.FPDFFont_Close(handle);
        _handles.Clear();
    }
}

internal sealed record TextStyle(
    double Size,
    uint Argb = 0xFF000000,
    string? Family = null,
    bool Bold = false,
    bool Italic = false,
    bool Invisible = false,
    string? Mark = null);

/// <summary>Adds text, rectangles and images to a page using visual coordinates (points, origin top-left, y down).</summary>
internal static unsafe class PageDraw
{
    /// <summary>Matrix placing upright text space at a visual baseline origin, rotated counter-clockwise as seen on screen.</summary>
    public static Affine TextToVisual(double x, double baselineY, double rotationDegrees = 0, double horizontalScale = 1)
    {
        var a = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        return new Affine(cos * horizontalScale, -sin * horizontalScale, -sin, -cos, x, baselineY);
    }

    /// <summary>
    /// Draws text with its baseline starting at (x, baselineY). Characters the chosen family lacks are drawn with a
    /// fallback font. Returns the created page objects. They go on top of the page, or with
    /// <paramref name="insertAt"/> before the object that is at that position now.
    /// </summary>
    public static List<nint> DrawText(PdfPage page, DocFonts fonts, string text, TextStyle style, double x, double baselineY,
        double rotationDegrees = 0, double horizontalScale = 1, int insertAt = -1)
    {
        var toPage = page.VisualToPage;
        var a = rotationDegrees * Math.PI / 180;
        return DrawTextWithMatrix(page, fonts, text, style, advance =>
        {
            var along = advance * horizontalScale;
            return TextToVisual(x + along * Math.Cos(a), baselineY - along * Math.Sin(a), rotationDegrees, horizontalScale)
                .Then(toPage);
        }, insertAt);
    }

    /// <summary>
    /// Draws text where the caller supplies the text-space → page-space matrix for a given advance along the baseline
    /// (in text units, i.e. points at the style's size).
    /// </summary>
    public static List<nint> DrawTextWithMatrix(PdfPage page, DocFonts fonts, string text, TextStyle style, Func<double, Affine> matrixAt,
        int insertAt = -1)
    {
        var created = new List<nint>();
        double advance = 0;
        foreach (var (run, font) in FontResolver.Itemize(text, style.Family, style.Bold, style.Italic))
        {
            var handle = fonts.Get(font, run);
            var obj = Native.FPDFPageObj_CreateTextObj(page.Doc.Handle, handle, (float)style.Size);
            if (obj == 0) throw new PdfException("Could not create a text object.");
            if (Native.FPDFText_SetText(obj, run) == 0)
            {
                Native.FPDFPageObj_Destroy(obj);
                throw new PdfException("Could not set the text.");
            }

            SetFill(obj, style.Argb);
            if (style.Invisible) Native.FPDFTextObj_SetTextRenderMode(obj, Native.FPDF_TEXTRENDERMODE_INVISIBLE);
            if (style.Mark != null) Native.FPDFPageObj_AddMark(obj, style.Mark);
            var m = matrixAt(advance);
            Native.FPDFPageObj_Transform(obj, m.A, m.B, m.C, m.D, m.E, m.F);
            if (insertAt < 0) Native.FPDFPage_InsertObject(page.Handle, obj);
            else Native.FPDFPage_InsertObjectAtIndex(page.Handle, obj, (nuint)(insertAt + created.Count));
            created.Add(obj);
            advance += font.Measure(run, style.Size);
        }

        return created;
    }

    public static nint DrawRect(PdfPage page, RectD visual, uint argb)
    {
        var m = page.VisualToPage;
        var (x0, y0) = m.Apply(visual.Left, visual.Top);
        var (x1, y1) = m.Apply(visual.Right, visual.Bottom);
        var obj = Native.FPDFPageObj_CreateNewRect(
            (float)Math.Min(x0, x1), (float)Math.Min(y0, y1), (float)Math.Abs(x1 - x0), (float)Math.Abs(y1 - y0));
        if (obj == 0) throw new PdfException("Could not create a rectangle.");
        SetFill(obj, argb);
        Native.FPDFPath_SetDrawMode(obj, Native.FPDF_FILLMODE_ALTERNATE, 0);
        Native.FPDFPage_InsertObject(page.Handle, obj);
        return obj;
    }

    /// <summary>
    /// Places an image so it fills <paramref name="visual"/>, optionally rotated counter-clockwise around the rectangle's centre.
    /// Opaque images are stored as JPEG, images with transparency losslessly with an alpha mask.
    /// </summary>
    public static nint DrawImage(PdfPage page, SKBitmap bitmap, RectD visual, double rotationDegrees = 0, double opacity = 1,
        int jpegQuality = 90, string? mark = null)
    {
        var obj = Native.FPDFPageObj_NewImageObj(page.Doc.Handle);
        if (obj == 0) throw new PdfException("Could not create an image object.");
        var hasAlpha = opacity < 0.999 || HasTransparency(bitmap);
        if (hasAlpha) SetBitmap(page, obj, bitmap, opacity);
        else SetJpeg(page, obj, Imaging.ImageFilters.EncodeJpeg(bitmap, jpegQuality));

        // Image space is the unit square with y up: (0,0) bottom-left, (1,1) top-right.
        var a = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        var unitToVisual = new Affine(visual.Width, 0, 0, -visual.Height, -visual.Width / 2, visual.Height / 2)
            .Then(new Affine(cos, -sin, sin, cos, visual.CenterX, visual.CenterY));
        var m = unitToVisual.Then(page.VisualToPage);
        Native.FPDFImageObj_SetMatrix(obj, m.A, m.B, m.C, m.D, m.E, m.F);
        if (mark != null) Native.FPDFPageObj_AddMark(obj, mark);
        Native.FPDFPage_InsertObject(page.Handle, obj);
        return obj;
    }

    /// <summary>
    /// Places a picture read from a file so it fills <paramref name="visual"/>, stored the way it came: a JPEG file
    /// as it is, drawings and screenshots exactly, other photographs as JPEG.
    /// </summary>
    public static nint DrawPicture(PdfPage page, Operations.ImagePixels picture, RectD visual)
    {
        var obj = Native.FPDFPageObj_NewImageObj(page.Doc.Handle);
        if (obj == 0) throw new PdfException("Could not create an image object.");
        if (picture.Jpeg is { } jpeg) SetJpeg(page, obj, jpeg);
        else if (picture.HasAlpha || picture.Lossless) SetBitmap(page, obj, picture.Bitmap);
        else SetJpeg(page, obj, Imaging.ImageFilters.EncodeJpeg(picture.Bitmap, 92));

        var m = new Affine(visual.Width, 0, 0, -visual.Height, visual.Left, visual.Bottom).Then(page.VisualToPage);
        Native.FPDFImageObj_SetMatrix(obj, m.A, m.B, m.C, m.D, m.E, m.F);
        Native.FPDFPage_InsertObject(page.Handle, obj);
        return obj;
    }

    public static void SetFill(nint obj, uint argb) =>
        Native.FPDFPageObj_SetFillColor(obj, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF, argb >> 24);

    public static uint GetFill(nint obj)
    {
        uint r, g, b, a;
        if (Native.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a) == 0) return 0xFF000000;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    public static void SetJpeg(PdfPage page, nint imageObject, byte[] jpeg)
    {
        var handle = GCHandle.Alloc(jpeg);
        try
        {
            var access = new FPDF_FILEACCESS
            {
                m_FileLen = new CULong((uint)jpeg.Length),
                m_GetBlock = &ReadBlock,
                m_Param = (void*)GCHandle.ToIntPtr(handle)
            };
            var pageHandle = page.Handle;
            if (Native.FPDFImageObj_LoadJpegFileInline(&pageHandle, 1, imageObject, &access) == 0)
                throw new PdfException("Could not store the image.");
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadBlock(void* param, CULong position, byte* buffer, CULong size)
    {
        var data = (byte[])GCHandle.FromIntPtr((nint)param).Target!;
        long offset = (long)position.Value, count = (long)size.Value;
        if (offset < 0 || offset + count > data.Length) return 0;
        data.AsSpan((int)offset, (int)count).CopyTo(new Span<byte>(buffer, (int)count));
        return 1;
    }

    /// <summary>Stores pixels losslessly. Accepts Gray8 and Bgra8888 bitmaps; alpha is multiplied by <paramref name="opacity"/>.</summary>
    public static void SetBitmap(PdfPage page, nint imageObject, SKBitmap bitmap, double opacity = 1)
    {
        var gray = bitmap.ColorType == SKColorType.Gray8;
        SKBitmap? converted = null;
        var source = bitmap;
        if (!gray && (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType == SKAlphaType.Premul || opacity < 0.999))
        {
            // PDFium wants straight (non-premultiplied) alpha.
            converted = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using (var pixmap = bitmap.PeekPixels())
                pixmap.ReadPixels(converted.Info, converted.GetPixels(), converted.RowBytes);

            if (opacity < 0.999)
            {
                var pixels = (uint*)converted.GetPixels();
                var total = converted.RowBytes / 4 * converted.Height;
                for (var i = 0; i < total; i++)
                {
                    var alpha = (uint)Math.Round((pixels[i] >> 24) * opacity);
                    pixels[i] = (pixels[i] & 0x00FFFFFF) | (alpha << 24);
                }
            }

            source = converted;
        }

        try
        {
            // Declared without an alpha channel when there is none, so no all-opaque mask is stored next to the picture.
            var format = gray ? Native.FPDFBitmap_Gray
                : source.AlphaType == SKAlphaType.Opaque ? Native.FPDFBitmap_BGRx
                : Native.FPDFBitmap_BGRA;
            var native = Native.FPDFBitmap_CreateEx(source.Width, source.Height, format, (void*)source.GetPixels(), source.RowBytes);
            if (native == 0) throw new PdfException("Could not allocate an image.");
            try
            {
                var pageHandle = page.Handle;
                if (Native.FPDFImageObj_SetBitmap(&pageHandle, 1, imageObject, native) == 0)
                    throw new PdfException("Could not store the image.");
            }
            finally
            {
                Native.FPDFBitmap_Destroy(native);
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque || bitmap.ColorType != SKColorType.Bgra8888) return false;
        var pixels = (uint*)bitmap.GetPixels();
        var stride = bitmap.RowBytes / 4;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (pixels[y * stride + x] >> 24 != 0xFF)
                return true;
        return false;
    }

    /// <summary>Copies a PDFium bitmap (as returned for image objects) into a Gray8 or opaque Bgra8888 Skia bitmap.</summary>
    public static SKBitmap? ToSkBitmap(nint nativeBitmap)
    {
        int w = Native.FPDFBitmap_GetWidth(nativeBitmap), h = Native.FPDFBitmap_GetHeight(nativeBitmap);
        var stride = Native.FPDFBitmap_GetStride(nativeBitmap);
        var buffer = (byte*)Native.FPDFBitmap_GetBuffer(nativeBitmap);
        var format = Native.FPDFBitmap_GetFormat(nativeBitmap);
        if (buffer == null || w <= 0 || h <= 0) return null;

        if (format == Native.FPDFBitmap_Gray)
        {
            var gray = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
            var dst = (byte*)gray.GetPixels();
            for (var y = 0; y < h; y++)
                Buffer.MemoryCopy(buffer + (long)y * stride, dst + (long)y * gray.RowBytes, w, w);
            return gray;
        }

        var bytesPerPixel = format == Native.FPDFBitmap_BGR ? 3 : format is Native.FPDFBitmap_BGRx or Native.FPDFBitmap_BGRA ? 4 : 0;
        if (bytesPerPixel == 0) return null;
        var color = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = (uint*)color.GetPixels();
        var rowPixels = color.RowBytes / 4;
        for (var y = 0; y < h; y++)
        {
            var src = buffer + (long)y * stride;
            var row = pixels + (long)y * rowPixels;
            for (var x = 0; x < w; x++, src += bytesPerPixel)
                row[x] = 0xFF000000u | ((uint)src[2] << 16) | ((uint)src[1] << 8) | src[0];
        }

        return color;
    }

    public static string GetObjectText(nint textObject, nint textPage)
    {
        var bytes = (int)Native.FPDFTextObj_GetText(textObject, textPage, null, new CULong(0)).Value;
        if (bytes <= 2) return "";
        var buffer = new byte[bytes];
        fixed (byte* p = buffer) Native.FPDFTextObj_GetText(textObject, textPage, p, new CULong((uint)bytes));
        return Encoding.Unicode.GetString(buffer, 0, bytes - 2);
    }

    public static string GetPageText(nint textPage)
    {
        var count = Native.FPDFText_CountChars(textPage);
        if (count <= 0) return "";
        var buffer = new ushort[count + 1];
        int written;
        fixed (ushort* p = buffer) written = Native.FPDFText_GetText(textPage, 0, count, p);
        if (written <= 1) return "";
        fixed (ushort* p = buffer) return new string((char*)p, 0, written - 1);
    }
}
