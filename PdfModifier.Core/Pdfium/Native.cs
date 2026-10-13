using System.Runtime.InteropServices;

namespace PdfModifier.Core.Pdfium;

[StructLayout(LayoutKind.Sequential)]
internal struct FS_MATRIX
{
    public float a, b, c, d, e, f;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_RECTF
{
    public float left, top, right, bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_SIZEF
{
    public float width, height;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEWRITE
{
    public int version;
    public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, void*, CULong, int> WriteBlock;
    // Not part of the PDFium struct: PDFium only reads the two fields above, the callback reads this one.
    public nint target;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEACCESS
{
    public CULong m_FileLen;
    public delegate* unmanaged[Cdecl]<void*, CULong, byte*, CULong, int> m_GetBlock;
    public void* m_Param;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FPDF_IMAGEOBJ_METADATA
{
    public uint width;
    public uint height;
    public float horizontal_dpi;
    public float vertical_dpi;
    public uint bits_per_pixel;
    public int colorspace;
    public int marked_content_id;
}

/// <summary>P/Invoke surface for the PDFium C API. Names and signatures mirror the public headers.</summary>
internal static unsafe class Native
{
    private const string Lib = "pdfium";

    public const int FPDF_ERR_PASSWORD = 4;

    public const int FPDFBitmap_Gray = 1;
    public const int FPDFBitmap_BGR = 2;
    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_PRINTING = 0x800;

    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_SHADING = 4;
    public const int FPDF_PAGEOBJ_FORM = 5;

    public const int FPDF_FONT_TRUETYPE = 2;
    public const int FPDF_FILLMODE_ALTERNATE = 1;
    public const int FPDF_TEXTRENDERMODE_INVISIBLE = 3;
    public const int FPDF_TEXTRENDERMODE_CLIP = 7;

    public const uint FPDF_NO_INCREMENTAL = 2;
    public const uint FPDF_REMOVE_SECURITY = 3;

    public const int FLAT_NORMALDISPLAY = 0;
    public const int FLAT_PRINT = 1;
    public const int FLATTEN_FAIL = 0;

    public const int FPDF_ANNOT_WATERMARK = 24;

    // ---- fpdfview.h ----
    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern nint FPDF_LoadMemDocument64(byte* data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Lib)] public static extern CULong FPDF_GetLastError();
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(nint document);
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(nint document);
    [DllImport(Lib)] public static extern nint FPDF_LoadPage(nint document, int pageIndex);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(nint page);
    [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(nint page);
    [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(nint page);
    [DllImport(Lib)] public static extern int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, FS_SIZEF* size);
    [DllImport(Lib)] public static extern int FPDF_GetPageBoundingBox(nint page, FS_RECTF* rect);
    [DllImport(Lib)] public static extern nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetFormat(nint bitmap);
    [DllImport(Lib)] public static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, CULong color);
    [DllImport(Lib)] public static extern void* FPDFBitmap_GetBuffer(nint bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetWidth(nint bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetHeight(nint bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetStride(nint bitmap);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(nint bitmap);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, double* pageX, double* pageY);

    // ---- fpdf_doc.h ----
    [DllImport(Lib)] public static extern nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);
    [DllImport(Lib)] public static extern nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);
    [DllImport(Lib)] public static extern nint FPDFBookmark_GetDest(nint document, nint bookmark);
    [DllImport(Lib)] public static extern int FPDFDest_GetDestPageIndex(nint document, nint dest);
    [DllImport(Lib)] public static extern CULong FPDF_GetMetaText(nint document, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, void* buffer, CULong buflen);

    // ---- fpdf_edit.h ----
    [DllImport(Lib)] public static extern nint FPDF_CreateNewDocument();
    [DllImport(Lib)] public static extern nint FPDFPage_New(nint document, int pageIndex, double width, double height);
    [DllImport(Lib)] public static extern void FPDFPage_Delete(nint document, int pageIndex);
    [DllImport(Lib)] public static extern int FPDF_MovePages(nint document, int* pageIndices, CULong pageIndicesLen, int destPageIndex);
    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(nint page);
    [DllImport(Lib)] public static extern void FPDFPage_SetRotation(nint page, int rotate);
    [DllImport(Lib)] public static extern void FPDFPage_InsertObject(nint page, nint pageObject);
    [DllImport(Lib)] public static extern int FPDFPage_InsertObjectAtIndex(nint page, nint pageObject, nuint index);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveObject(nint page, nint pageObject);
    [DllImport(Lib)] public static extern int FPDFPage_CountObjects(nint page);
    [DllImport(Lib)] public static extern nint FPDFPage_GetObject(nint page, int index);
    [DllImport(Lib)] public static extern int FPDFPage_GenerateContent(nint page);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(nint pageObject);
    [DllImport(Lib)] public static extern int FPDFPageObj_HasTransparency(nint pageObject);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetType(nint pageObject);
    [DllImport(Lib)] public static extern void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetMatrix(nint pageObject, FS_MATRIX* matrix);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetMatrix(nint pageObject, FS_MATRIX* matrix);
    [DllImport(Lib)] public static extern void FPDFPage_TransformAnnots(nint page, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern nint FPDFPageObj_NewImageObj(nint document);
    [DllImport(Lib)] public static extern int FPDFPageObj_CountMarks(nint pageObject);
    [DllImport(Lib)] public static extern nint FPDFPageObj_GetMark(nint pageObject, CULong index);
    [DllImport(Lib)] public static extern nint FPDFPageObj_AddMark(nint pageObject, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern int FPDFPageObj_RemoveMark(nint pageObject, nint mark);
    [DllImport(Lib)] public static extern int FPDFPageObjMark_GetName(nint mark, void* buffer, CULong buflen, CULong* outBuflen);
    [DllImport(Lib)] public static extern int FPDFPageObjMark_GetParamStringValue(nint mark, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, void* buffer, CULong buflen, CULong* outBuflen);
    [DllImport(Lib)] public static extern int FPDFPageObjMark_GetParamIntValue(nint mark, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, int* outValue);
    [DllImport(Lib)] public static extern int FPDFPageObjMark_SetIntParam(nint document, nint pageObject, nint mark, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, int value);
    [DllImport(Lib)] public static extern int FPDFPageObjMark_SetStringParam(nint document, nint pageObject, nint mark, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetIsActive(nint pageObject, int active);
    [DllImport(Lib)] public static extern nint FPDFImageObj_GetRenderedBitmap(nint document, nint page, nint imageObject);
    [DllImport(Lib)] public static extern int FPDFImageObj_GetImagePixelSize(nint imageObject, uint* width, uint* height);
    [DllImport(Lib)] public static extern int FPDFImageObj_LoadJpegFileInline(nint* pages, int count, nint imageObject, FPDF_FILEACCESS* fileAccess);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetMatrix(nint imageObject, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObject, nint bitmap);
    [DllImport(Lib)] public static extern nint FPDFImageObj_GetBitmap(nint imageObject);
    [DllImport(Lib)] public static extern CULong FPDFImageObj_GetImageDataRaw(nint imageObject, void* buffer, CULong buflen);
    [DllImport(Lib)] public static extern int FPDFImageObj_GetImageFilterCount(nint imageObject);
    [DllImport(Lib)] public static extern CULong FPDFImageObj_GetImageFilter(nint imageObject, int index, void* buffer, CULong buflen);
    [DllImport(Lib)] public static extern int FPDFImageObj_GetImageMetadata(nint imageObject, nint page, FPDF_IMAGEOBJ_METADATA* metadata);
    [DllImport(Lib)] public static extern nint FPDFPageObj_CreateNewRect(float x, float y, float w, float h);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetBounds(nint pageObject, float* left, float* bottom, float* right, float* top);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetFillColor(nint pageObject, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetFillColor(nint pageObject, uint* r, uint* g, uint* b, uint* a);
    [DllImport(Lib)] public static extern int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);
    [DllImport(Lib)] public static extern int FPDFText_SetText(nint textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);
    [DllImport(Lib)] public static extern nint FPDFText_LoadStandardFont(nint document, [MarshalAs(UnmanagedType.LPUTF8Str)] string font);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetFontSize(nint text, float* size);
    [DllImport(Lib)] public static extern void FPDFFont_Close(nint font);
    [DllImport(Lib)] public static extern nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetTextRenderMode(nint text);
    [DllImport(Lib)] public static extern int FPDFTextObj_SetTextRenderMode(nint text, int renderMode);
    [DllImport(Lib)] public static extern CULong FPDFTextObj_GetText(nint textObject, nint textPage, void* buffer, CULong length);
    [DllImport(Lib)] public static extern nint FPDFTextObj_GetFont(nint text);
    [DllImport(Lib)] public static extern nuint FPDFFont_GetBaseFontName(nint font, byte* buffer, nuint length);
    [DllImport(Lib)] public static extern int FPDFFont_GetFlags(nint font);
    [DllImport(Lib)] public static extern int FPDFFont_GetWeight(nint font);
    [DllImport(Lib)] public static extern int FPDFFont_GetIsEmbedded(nint font);
    [DllImport(Lib)] public static extern int FPDFFormObj_CountObjects(nint formObject);
    [DllImport(Lib)] public static extern nint FPDFFormObj_GetObject(nint formObject, CULong index);

    // ---- fpdf_text.h ----
    [DllImport(Lib)] public static extern nint FPDFText_LoadPage(nint page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(nint textPage);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(nint textPage);
    [DllImport(Lib)] public static extern int FPDFText_GetText(nint textPage, int startIndex, int count, ushort* result);
    [DllImport(Lib)] public static extern uint FPDFText_GetUnicode(nint textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_IsGenerated(nint textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);
    [DllImport(Lib)] public static extern nint FPDFText_GetTextObject(nint textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_GetFillColor(nint textPage, int index, uint* r, uint* g, uint* b, uint* a);

    // ---- fpdf_ppo.h ----
    [DllImport(Lib)] public static extern int FPDF_ImportPagesByIndex(nint dest, nint src, int* pageIndices, CULong length, int index);
    [DllImport(Lib)] public static extern int FPDF_CopyViewerPreferences(nint dest, nint src);
    [DllImport(Lib)] public static extern nint FPDF_NewXObjectFromPage(nint destDoc, nint srcDoc, int srcPageIndex);
    [DllImport(Lib)] public static extern void FPDF_CloseXObject(nint xobject);
    [DllImport(Lib)] public static extern nint FPDF_NewFormObjectFromXObject(nint xobject);

    // ---- fpdf_save.h ----
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(nint document, FPDF_FILEWRITE* fileWrite, CULong flags);

    // ---- fpdf_flatten.h ----
    [DllImport(Lib)] public static extern int FPDFPage_Flatten(nint page, int flag);

    // ---- fpdf_transformpage.h ----
    [DllImport(Lib)] public static extern void FPDFPage_SetMediaBox(nint page, float left, float bottom, float right, float top);
    [DllImport(Lib)] public static extern void FPDFPage_SetCropBox(nint page, float left, float bottom, float right, float top);
    [DllImport(Lib)] public static extern int FPDFPage_GetMediaBox(nint page, float* left, float* bottom, float* right, float* top);
    [DllImport(Lib)] public static extern int FPDFPage_GetCropBox(nint page, float* left, float* bottom, float* right, float* top);
    [DllImport(Lib)] public static extern int FPDFPage_TransFormWithClip(nint page, FS_MATRIX* matrix, FS_RECTF* clipRect);

    // ---- fpdf_annot.h ----
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(nint page);
    [DllImport(Lib)] public static extern nint FPDFPage_GetAnnot(nint page, int index);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(nint annot);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(nint page, int index);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(nint annot);
}
