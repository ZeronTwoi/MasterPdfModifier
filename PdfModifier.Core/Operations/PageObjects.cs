using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfModifier.Core.Fonts;
using PdfModifier.Core.Imaging;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

public enum TextAlign
{
    Left,
    Center,
    Right
}

/// <summary>
/// Something this app put on a page and can still change afterwards: move, resize, restyle or take away again.
/// <see cref="Bounds"/> is the upright box in visual page coordinates; the object is shown turned by
/// <see cref="Rotation"/> degrees (counter-clockwise as seen) around the centre of that box.
/// <see cref="Id"/> is unique on its page and chosen by the caller.
/// </summary>
public abstract record PageObject
{
    public int Id { get; init; }
    public RectD Bounds { get; init; }
    public double Rotation { get; init; }
}

/// <summary>
/// A box of text. Its width is what the lines are wrapped to; the height follows from the text
/// (see <see cref="PdfPageObjects.Fit"/>).
/// </summary>
public sealed record PageText : PageObject
{
    public string Text { get; init; } = "";

    /// <summary>An installed family, "serif", "monospace", or null for the default sans-serif.</summary>
    public string? FontFamily { get; init; }

    public double FontSize { get; init; } = 12;
    public uint Color { get; init; } = 0xFF000000;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public TextAlign Align { get; init; }

    /// <summary>Distance between baselines as a multiple of the font size.</summary>
    public double LineSpacing { get; init; } = 1.2;
}

public sealed record PageImage : PageObject
{
    public required ImagePixels Pixels { get; init; }
    public double Opacity { get; init; } = 1;

    /// <summary>Counts changes to the pixels, so an unchanged picture is not stored a second time.</summary>
    public int Version { get; init; }
}

/// <summary>A filled rectangle that hides what is underneath it.</summary>
public sealed record PagePatch : PageObject
{
    public uint Color { get; init; } = 0xFFFFFFFF;
}

/// <summary>The picture of a <see cref="PageImage"/>. Copies of the object share it; it is never changed in place.</summary>
public sealed class ImagePixels
{
    internal ImagePixels(SKBitmap bitmap, bool hasAlpha, byte[]? jpeg = null, bool lossless = true)
    {
        Bitmap = bitmap;
        HasAlpha = hasAlpha;
        Jpeg = jpeg;
        Lossless = lossless;
    }

    /// <summary>BGRA pixels with straight (not premultiplied) alpha.</summary>
    public SKBitmap Bitmap { get; }

    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;
    public bool HasAlpha { get; }

    /// <summary>The JPEG file the picture came from, stored as it is so it does not lose quality by being re-encoded.</summary>
    internal byte[]? Jpeg { get; }

    /// <summary>True for drawings, logos and screenshots, whose sharp edges JPEG would smear.</summary>
    internal bool Lossless { get; }

    /// <summary>Reads a PNG, JPEG, WebP, BMP or GIF file.</summary>
    public static unsafe ImagePixels FromFile(byte[] data)
    {
        using var decoded = PdfImages.Decode(data, out var format, out var turned);
        var transparent = false;
        if (decoded.AlphaType != SKAlphaType.Opaque)
        {
            var pixels = (uint*)decoded.GetPixels();
            var total = decoded.RowBytes / 4 * decoded.Height;
            for (var i = 0; i < total && !transparent; i++) transparent = pixels[i] >> 24 != 0xFF;
        }

        var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888,
            transparent ? SKAlphaType.Unpremul : SKAlphaType.Opaque));
        using (var pixmap = decoded.PeekPixels())
            pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);

        var jpeg = format == SKEncodedImageFormat.Jpeg && !turned && JpegChannels(data) is 1 or 3 ? data : null;
        var lossless = format is SKEncodedImageFormat.Png or SKEncodedImageFormat.Bmp or SKEncodedImageFormat.Gif &&
                       (long)bitmap.Width * bitmap.Height <= 4_000_000;
        return new ImagePixels(bitmap, transparent, jpeg, lossless);
    }

    /// <summary>Number of colour channels of a JPEG file, or 0 when it cannot be told. Four means CMYK.</summary>
    private static int JpegChannels(byte[] data)
    {
        for (var i = 2; i + 9 < data.Length;)
        {
            if (data[i] != 0xFF) return 0;
            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte before a marker
                continue;
            }

            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }

            // Start-of-frame markers; C4, C8 and CC in the same range are tables, not frames.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return data[i + 9];
            i += 2 + ((data[i + 2] << 8) | data[i + 3]);
        }

        return 0;
    }
}

/// <summary>Text of a box drawn as a picture, for showing it while it is being edited.</summary>
/// <param name="Bitmap">Premultiplied BGRA on a transparent background.</param>
/// <param name="Padding">Points of empty margin around the box on every side, so overhanging letters are not cut off.</param>
public sealed record TextPicture(SKBitmap Bitmap, double Padding);

internal static class Frames
{
    /// <summary>
    /// Maps box coordinates (points from the box's own top-left corner, y down, before turning) to the page as displayed.
    /// </summary>
    public static Affine BoxToVisual(RectD bounds, double rotation)
    {
        var a = rotation * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a), hw = bounds.Width / 2, hh = bounds.Height / 2;
        return new Affine(cos, -sin, sin, cos, bounds.CenterX - hw * cos - hh * sin, bounds.CenterY + hw * sin - hh * cos);
    }

    /// <summary>The upright box whose turned top-left corner lies at (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public static RectD BoxAt(double left, double top, double width, double height, double rotation)
    {
        var a = rotation * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        var cx = left + width / 2 * cos + height / 2 * sin;
        var cy = top - width / 2 * sin + height / 2 * cos;
        return new RectD(cx - width / 2, cy - height / 2, cx + width / 2, cy + height / 2);
    }

    /// <summary>Reads box, rotation and scale back out of a box-to-visual matrix, for a box of the given own size.</summary>
    public static (RectD Bounds, double Rotation, double Scale) Decompose(Affine m, double width, double height)
    {
        var sx = Math.Sqrt(m.A * m.A + m.B * m.B);
        var sy = Math.Sqrt(m.C * m.C + m.D * m.D);
        var rotation = Math.Atan2(-m.B, m.A) * 180 / Math.PI;
        if (Math.Abs(rotation) < 1e-4) rotation = 0;
        var (cx, cy) = m.Apply(width / 2, height / 2);
        double w = width * sx, h = height * sy;
        return (new RectD(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), rotation, sx);
    }
}

/// <summary>
/// Reads and writes the editable objects of a page. They are ordinary page content, tagged so that they can be told
/// apart from what the document contained before and changed again later, also after the file was saved and reopened.
/// </summary>
public static unsafe class PdfPageObjects
{
    /// <summary>Marked-content tag carried by every piece of an editable object.</summary>
    internal const string Mark = "PMObj";

    private const string TextKind = "t", ImageKind = "i", PatchKind = "p";

    private sealed class Stored
    {
        public int Id;
        public string Kind = "";
        public JsonObject Definition = new();
        public nint Head, HeadMark;
        public readonly List<nint> Handles = [];
    }

    /// <summary>The installed font families text can be set in.</summary>
    public static IReadOnlyList<string> FontFamilies() => FontResolver.EmbeddableFamilies();

    /// <summary>The editable objects of a page, bottom-most first.</summary>
    public static IReadOnlyList<PageObject> Read(byte[] pdf, int pageIndex)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var page = doc.LoadPage(pageIndex);
            return Scan(page).Select(stored => ToObject(doc, page, stored, true)).OfType<PageObject>().ToList();
        }
    }

    /// <summary>
    /// Makes the editable objects of a page exactly <paramref name="objects"/>, bottom-most first: objects not listed are
    /// taken off the page, new ones are added, changed ones are updated where they are. The objects keep their place
    /// between the other content of the page, so something stamped over them later (a watermark, page numbers) stays on top.
    /// Text of the original document lying inside one of the <paramref name="eraseUnder"/> areas is removed as well, so
    /// it is not left behind for search and copy. Returns the input instance when nothing had to change.
    /// </summary>
    public static byte[] Write(byte[] pdf, int pageIndex, IReadOnlyList<PageObject> objects, IReadOnlyCollection<RectD>? eraseUnder = null)
    {
        if (objects.Any(o => o.Id <= 0) || objects.Select(o => o.Id).Distinct().Count() != objects.Count)
            throw new ArgumentException("Every object needs its own positive id.", nameof(objects));

        byte[] result;
        var imagesDropped = false;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var page = doc.LoadPage(pageIndex);
            using var fonts = new DocFonts(doc);
            var stored = Scan(page).ToDictionary(s => s.Id);
            var changed = false;

            var wanted = objects.Select(o => o.Id).ToHashSet();
            foreach (var gone in stored.Values.Where(s => !wanted.Contains(s.Id)).ToList())
            {
                RemoveAll(page, gone.Handles);
                stored.Remove(gone.Id);
                imagesDropped |= gone.Kind == ImageKind;
                changed = true;
            }

            // What is on the page already and only needs moving, fading or recolouring stays the object it is: a text
            // box keeps its font, an image its stored picture.
            var texts = objects.OfType<PageText>().Select(Fit).ToDictionary(t => t.Id);
            var current = new Dictionary<int, PageText>();
            var kept = new List<PageObject>();
            foreach (var obj in objects)
            {
                if (!stored.TryGetValue(obj.Id, out var existing) || existing.Kind != KindOf(obj)) continue;
                if (obj is PageText)
                {
                    if (ToObject(doc, page, existing, false) is not PageText now || !SameLayout(now, texts[obj.Id])) continue;
                    current[obj.Id] = now;
                }

                kept.Add(obj);
            }

            // An object already on the page cannot be restacked, only written anew at another depth. The largest group
            // that is in the wanted order as it is stays; the others are written again.
            var places = new Dictionary<nint, int>();
            var onPage = page.GetObjects();
            for (var i = 0; i < onPage.Count; i++) places[onPage[i]] = i;
            var inOrder = LongestRising(kept.Select(o => places[stored[o.Id].Head]).ToList());
            var keep = kept.Where((_, i) => inOrder.Contains(i)).Select(o => o.Id).ToHashSet();

            foreach (var text in texts.Values.Where(t => !keep.Contains(t.Id)))
                fonts.Prepare(text.Text, text.FontFamily, text.Bold, text.Italic);

            // From the top down, so each object that is written anew can be slipped in right under the one above it.
            nint above = 0;
            for (var i = objects.Count - 1; i >= 0; i--)
            {
                var obj = objects[i];
                stored.TryGetValue(obj.Id, out var existing);
                if (existing != null && keep.Contains(obj.Id))
                {
                    changed |= obj switch
                    {
                        PageText => MoveText(page, existing, current[obj.Id], texts[obj.Id]),
                        PageImage image => UpdateImage(page, image, existing, ref imagesDropped),
                        _ => UpdatePatch(page, (PagePatch)obj, existing)
                    };
                    above = existing.Handles[0];
                    continue;
                }

                // The file a photograph came from is taken over as it is; decoding and encoding it again would cost quality.
                byte[]? jpeg = null;
                if (existing != null)
                {
                    if (obj is PageImage { Pixels.HasAlpha: false } same && existing.Kind == ImageKind && (int)Number(existing.Definition, "v") == same.Version)
                        jpeg = RawJpeg(existing.Head);
                    RemoveAll(page, existing.Handles);
                    imagesDropped |= existing.Kind == ImageKind;
                    changed = true;
                }

                var at = above == 0 ? -1 : IndexOf(page, above);
                List<nint> created = obj switch
                {
                    PageText => CreateText(page, fonts, texts[obj.Id], at),
                    PageImage image => [CreateImage(page, image, at, jpeg)],
                    _ => [CreatePatch(page, (PagePatch)obj, at)]
                };
                if (created.Count == 0) continue;
                above = created[0];
                changed = true;
            }

            if (eraseUnder != null)
                foreach (var area in eraseUnder)
                    changed |= EraseTextUnder(page, area);

            if (!changed) return pdf;
            page.GenerateContent();
            result = doc.Save();
        }

        // A picture that was taken away or replaced is still stored in the file until it is cleaned out.
        return imagesDropped ? ImageObjects.DropReplacedImages(result) : result;
    }

    /// <summary>Gives a text box the height its text needs. The top-left corner stays where it is.</summary>
    public static PageText Fit(PageText text)
    {
        var height = TextLayout.Create(text).Height;
        if (Math.Abs(height - text.Bounds.Height) < 1e-6) return text;
        var (left, top) = Frames.BoxToVisual(text.Bounds, text.Rotation).Apply(0, 0);
        return text with { Bounds = Frames.BoxAt(left, top, text.Bounds.Width, height, text.Rotation) };
    }

    /// <summary>How far below the top of its box the first line of a text box stands.</summary>
    public static double BaselineOffset(PageText text) => TextLayout.Create(text).Lines[0].Baseline;

    /// <summary>Width of the widest line the text would need if it were not wrapped at all.</summary>
    public static double NaturalWidth(PageText text) =>
        TextLayout.Create(text with { Bounds = new RectD(0, 0, double.MaxValue / 4, 0) }).Lines.Max(line => line.Width);

    /// <summary>Draws a text box (upright, without its rotation) at <paramref name="scale"/> pixels per point.</summary>
    public static TextPicture RenderText(PageText text, double scale)
    {
        var layout = TextLayout.Create(text);
        var padding = Math.Ceiling(text.FontSize * 0.35);
        double width = text.Bounds.Width + 2 * padding, height = layout.Height + 2 * padding;
        // Very large boxes at a deep zoom are drawn coarser rather than exhausting memory.
        scale = Math.Min(scale, Math.Sqrt(24_000_000 / Math.Max(1, width * height)));
        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)),
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)scale);
        canvas.Translate((float)padding, (float)padding);
        var color = new SKColor(text.Color | 0xFF000000);
        foreach (var line in layout.Lines)
        {
            var x = line.X;
            foreach (var (run, font) in FontResolver.Itemize(line.Text, text.FontFamily, text.Bold, text.Italic))
            {
                font.Draw(canvas, run, x, line.Baseline, text.FontSize, color);
                x += font.Measure(run, text.FontSize);
            }
        }

        return new TextPicture(bitmap, padding);
    }

    /// <summary>
    /// Makes the part of an image that lies inside <paramref name="area"/> (visual coordinates) see-through.
    /// Returns the image unchanged when the area misses it, and null when nothing of the image would be left.
    /// </summary>
    public static PageImage? CutOut(PageImage image, RectD area)
    {
        var source = image.Pixels.Bitmap;
        int w = source.Width, h = source.Height;
        var toVisual = new Affine(image.Bounds.Width / w, 0, 0, image.Bounds.Height / h, 0, 0)
            .Then(Frames.BoxToVisual(image.Bounds, image.Rotation));
        var toPixel = toVisual.Invert();
        var reach = toPixel.MapBounds(area.Left, area.Top, area.Right, area.Bottom);
        int x0 = Math.Max(0, (int)Math.Floor(reach.Left)), x1 = Math.Min(w, (int)Math.Ceiling(reach.Right));
        int y0 = Math.Max(0, (int)Math.Floor(reach.Top)), y1 = Math.Min(h, (int)Math.Ceiling(reach.Bottom));
        if (x0 >= x1 || y0 >= y1) return image;

        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var pixmap = source.PeekPixels())
            pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
        var pixels = (uint*)bitmap.GetPixels();
        var stride = bitmap.RowBytes / 4;
        var cleared = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var (vx, vy) = toVisual.Apply(x + 0.5, y + 0.5);
            if (!area.Contains(vx, vy) || pixels[y * stride + x] >> 24 == 0) continue;
            pixels[y * stride + x] = 0;
            cleared++;
        }

        if (cleared == 0)
        {
            bitmap.Dispose();
            return image;
        }

        var left = false;
        for (var i = 0; i < stride * h && !left; i++) left = pixels[i] >> 24 != 0;
        if (!left)
        {
            bitmap.Dispose();
            return null;
        }

        return image with { Pixels = new ImagePixels(bitmap, true), Version = image.Version + 1 };
    }

    /// <summary>
    /// The paper colour around an area and the colour of what is written inside it, taken from a picture of the page
    /// drawn at <paramref name="pixelsPerPoint"/>.
    /// </summary>
    public static (uint Background, uint Ink) SampleColors(SKBitmap page, double pixelsPerPoint, RectD area) =>
        ImageAnalysis.SampleRegionColors(page,
            (int)(area.Left * pixelsPerPoint), (int)(area.Top * pixelsPerPoint),
            (int)Math.Ceiling(area.Right * pixelsPerPoint), (int)Math.Ceiling(area.Bottom * pixelsPerPoint));

    /// <summary>
    /// Puts a patch on top of a page that is being edited by another operation. It gets the next free id, so it shows
    /// up as an object that can be moved, recoloured or taken away again.
    /// </summary>
    internal static void AddPatch(PdfPage page, RectD area, uint color)
    {
        var id = Scan(page).Select(s => s.Id).DefaultIfEmpty(0).Max() + 1;
        foreach (var obj in page.GetObjects())
            if (FindMark(obj, out _, out var used))
                id = Math.Max(id, used + 1);
        CreatePatch(page, new PagePatch { Id = id, Bounds = area, Color = color }, -1);
    }

    /// <summary>True for page content that belongs to an editable object.</summary>
    internal static bool IsEditable(nint obj) => FindMark(obj, out _, out _);

    // ---- reading ------------------------------------------------------------------------------------------------

    private static List<Stored> Scan(PdfPage page)
    {
        var groups = new List<Stored>();
        var byId = new Dictionary<int, Stored>();
        foreach (var obj in page.GetObjects())
        {
            if (!FindMark(obj, out var mark, out var id)) continue;
            if (!byId.TryGetValue(id, out var stored))
            {
                byId[id] = stored = new Stored { Id = id };
                groups.Add(stored);
            }

            stored.Handles.Add(obj);
            if (stored.Head != 0 || ReadDefinition(mark) is not { } definition) continue;
            stored.Head = obj;
            stored.HeadMark = mark;
            stored.Definition = definition;
            stored.Kind = definition["k"]?.GetValue<string>() ?? "";
        }

        // Pieces whose description was lost cannot be put back together; they stay on the page as ordinary content.
        groups.RemoveAll(s => s.Head == 0 || s.Kind is not (TextKind or ImageKind or PatchKind));
        return groups;
    }

    private static bool FindMark(nint obj, out nint found, out int id)
    {
        found = 0;
        id = 0;
        var count = Native.FPDFPageObj_CountMarks(obj);
        if (count <= 0) return false;
        var name = stackalloc byte[32];
        for (var i = 0; i < count; i++)
        {
            var mark = Native.FPDFPageObj_GetMark(obj, new CULong((uint)i));
            CULong length;
            if (mark == 0 || Native.FPDFPageObjMark_GetName(mark, name, new CULong(32), &length) == 0) continue;
            if (length.Value != (uint)(Mark.Length + 1) * 2 || Encoding.Unicode.GetString(name, Mark.Length * 2) != Mark) continue;
            int value;
            if (Native.FPDFPageObjMark_GetParamIntValue(mark, "Id", &value) == 0 || value <= 0) continue;
            found = mark;
            id = value;
            return true;
        }

        return false;
    }

    private static JsonObject? ReadDefinition(nint mark)
    {
        // Long descriptions are spread over several entries: a string in page content may not exceed 32 KB.
        var json = new StringBuilder();
        for (var part = 0; ; part++)
        {
            var key = part == 0 ? "D" : "D" + part;
            CULong length;
            if (Native.FPDFPageObjMark_GetParamStringValue(mark, key, null, new CULong(0), &length) == 0 || length.Value <= 2) break;
            var buffer = new byte[(int)length.Value];
            fixed (byte* p = buffer)
                if (Native.FPDFPageObjMark_GetParamStringValue(mark, key, p, new CULong((uint)buffer.Length), &length) == 0)
                    break;
            json.Append(Encoding.Unicode.GetString(buffer, 0, buffer.Length - 2));
        }

        if (json.Length == 0) return null;
        try
        {
            return JsonNode.Parse(json.ToString()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteDefinition(PdfDoc doc, nint obj, nint mark, JsonObject definition)
    {
        const int partLength = 16000;
        // The default encoder escapes everything outside ASCII, so the text survives whatever encoding the string gets.
        var json = definition.ToJsonString();
        for (int part = 0, at = 0; at < json.Length; part++, at += partLength)
            Native.FPDFPageObjMark_SetStringParam(doc.Handle, obj, mark, part == 0 ? "D" : "D" + part,
                json.Substring(at, Math.Min(partLength, json.Length - at)));
    }

    private static Affine MatrixOf(nint obj)
    {
        FS_MATRIX m;
        return Native.FPDFPageObj_GetMatrix(obj, &m) != 0 ? new Affine(m.a, m.b, m.c, m.d, m.e, m.f) : Affine.Identity;
    }

    private static double Number(JsonObject definition, string key, double fallback = 0) =>
        definition[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;

    private static PageObject? ToObject(PdfDoc doc, PdfPage page, Stored stored, bool withPixels)
    {
        var d = stored.Definition;
        switch (stored.Kind)
        {
            case TextKind:
            {
                // The box is found from where its first piece of text sits now, so the object follows the page when
                // another tool scales or moves the content.
                var textToBox = new Affine(1, 0, 0, -1, Number(d, "ox"), Number(d, "oy"));
                var boxToVisual = textToBox.Invert().Then(MatrixOf(stored.Head)).Then(page.PageToVisual);
                var (bounds, rotation, scale) = Frames.Decompose(boxToVisual, Number(d, "w", 1), Number(d, "h", 1));
                return new PageText
                {
                    Id = stored.Id,
                    Bounds = bounds,
                    Rotation = rotation,
                    Text = d["t"]?.GetValue<string>() ?? "",
                    FontFamily = d["f"]?.GetValue<string>(),
                    FontSize = Number(d, "s", 12) * scale,
                    Color = PageDraw.GetFill(stored.Head) | 0xFF000000,
                    Bold = Number(d, "b") != 0,
                    Italic = Number(d, "i") != 0,
                    Align = (TextAlign)(int)Math.Clamp(Number(d, "a"), 0, 2),
                    LineSpacing = Number(d, "l", 1.2)
                };
            }
            case ImageKind:
            {
                var (bounds, rotation, _) = Frames.Decompose(UnitToVisual(page, stored.Head), 1, 1);
                var opacity = Math.Clamp(Number(d, "o", 1), 0, 1);
                var pixels = withPixels ? ReadPixels(doc, page, stored.Head, opacity) : Placeholder;
                return pixels == null
                    ? null
                    : new PageImage { Id = stored.Id, Bounds = bounds, Rotation = rotation, Pixels = pixels, Opacity = opacity, Version = (int)Number(d, "v") };
            }
            default:
            {
                var (bounds, rotation, _) = Frames.Decompose(UnitToVisual(page, stored.Head), 1, 1);
                return new PagePatch { Id = stored.Id, Bounds = bounds, Rotation = rotation, Color = PageDraw.GetFill(stored.Head) | 0xFF000000 };
            }
        }
    }

    /// <summary>Stands in for the pixels where only the placement of an image is of interest.</summary>
    private static readonly ImagePixels Placeholder =
        new(new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque)), false);

    /// <summary>Images and patches are unit squares placed by their matrix; this is that placement, top-left origin, as displayed.</summary>
    internal static Affine UnitToVisual(PdfPage page, nint obj) =>
        new Affine(1, 0, 0, -1, 0, 1).Then(MatrixOf(obj)).Then(page.PageToVisual);

    private static Affine UnitToPage(PdfPage page, PageObject obj) =>
        new Affine(1, 0, 0, -1, 0, 1)
            .Then(new Affine(obj.Bounds.Width, 0, 0, obj.Bounds.Height, 0, 0))
            .Then(Frames.BoxToVisual(obj.Bounds, obj.Rotation))
            .Then(page.VisualToPage);

    private static ImagePixels? ReadPixels(PdfDoc doc, PdfPage page, nint obj, double opacity)
    {
        uint width, height;
        if (Native.FPDFImageObj_GetImagePixelSize(obj, &width, &height) == 0 || width == 0 || height == 0) return null;
        // Enormous pictures are read at a reduced size; they would not fit in memory several times over.
        var shrink = Math.Min(1, Math.Sqrt(40_000_000.0 / ((double)width * height)));
        int w = Math.Max(1, (int)(width * shrink)), h = Math.Max(1, (int)(height * shrink));

        // Drawn alone, one pixel per unit and fully opaque, the image comes out exactly as stored, mask included.
        var placement = MatrixOf(obj);
        Native.FPDFPageObj_SetFillColor(obj, 0, 0, 0, 255);
        Native.FPDFImageObj_SetMatrix(obj, w, 0, 0, h, 0, 0);
        var rendered = Native.FPDFImageObj_GetRenderedBitmap(doc.Handle, page.Handle, obj);
        Native.FPDFImageObj_SetMatrix(obj, placement.A, placement.B, placement.C, placement.D, placement.E, placement.F);
        Native.FPDFPageObj_SetFillColor(obj, 0, 0, 0, (uint)Math.Round(opacity * 255));
        if (rendered == 0) return null;
        try
        {
            if (Native.FPDFBitmap_GetFormat(rendered) != Native.FPDFBitmap_BGRA) return null;
            int rw = Native.FPDFBitmap_GetWidth(rendered), rh = Native.FPDFBitmap_GetHeight(rendered), stride = Native.FPDFBitmap_GetStride(rendered);
            var source = (byte*)Native.FPDFBitmap_GetBuffer(rendered);
            if (source == null || rw <= 0 || rh <= 0) return null;

            var transparent = false;
            for (var y = 0; y < rh && !transparent; y++)
            {
                var row = (uint*)(source + (long)y * stride);
                for (var x = 0; x < rw && !transparent; x++) transparent = row[x] >> 24 != 0xFF;
            }

            var bitmap = new SKBitmap(new SKImageInfo(rw, rh, SKColorType.Bgra8888, transparent ? SKAlphaType.Unpremul : SKAlphaType.Opaque));
            var target = (byte*)bitmap.GetPixels();
            for (var y = 0; y < rh; y++)
                Buffer.MemoryCopy(source + (long)y * stride, target + (long)y * bitmap.RowBytes, bitmap.RowBytes, rw * 4);
            // Already in the document, so the choice between JPEG and lossless only matters if it is stored again.
            return new ImagePixels(bitmap, transparent);
        }
        finally
        {
            Native.FPDFBitmap_Destroy(rendered);
        }
    }

    // ---- writing ------------------------------------------------------------------------------------------------

    private static string KindOf(PageObject obj) => obj switch
    {
        PageText => TextKind,
        PageImage => ImageKind,
        _ => PatchKind
    };

    private static void RemoveAll(PdfPage page, List<nint> handles)
    {
        foreach (var handle in handles) page.RemoveObject(handle);
    }

    private static nint Tag(PdfDoc doc, nint obj, int id, JsonObject? definition)
    {
        var mark = Native.FPDFPageObj_AddMark(obj, Mark);
        if (mark == 0) throw new PdfException("Could not tag a page object.");
        Native.FPDFPageObjMark_SetIntParam(doc.Handle, obj, mark, "Id", id);
        if (definition != null) WriteDefinition(doc, obj, mark, definition);
        return mark;
    }

    private static double Rounded(double value) => Math.Round(value, 4);

    /// <summary>True when the two boxes break into the same lines, so one can become the other by moving and scaling.</summary>
    private static bool SameLayout(PageText current, PageText wanted)
    {
        if (current.Text != wanted.Text || current.FontFamily != wanted.FontFamily || current.Bold != wanted.Bold ||
            current.Italic != wanted.Italic || current.Align != wanted.Align) return false;
        if (Math.Abs(current.LineSpacing - wanted.LineSpacing) > 1e-6) return false;
        double a = current.Bounds.Width / current.FontSize, b = wanted.Bounds.Width / wanted.FontSize;
        return Math.Abs(a - b) <= 1e-4 * Math.Max(1, b);
    }

    private static bool MoveText(PdfPage page, Stored stored, PageText current, PageText wanted)
    {
        var changed = false;
        if ((current.Color & 0xFFFFFF) != (wanted.Color & 0xFFFFFF))
        {
            foreach (var handle in stored.Handles) PageDraw.SetFill(handle, wanted.Color | 0xFF000000);
            changed = true;
        }

        var factor = wanted.FontSize / current.FontSize;
        var move = Frames.BoxToVisual(current.Bounds, current.Rotation).Then(page.VisualToPage).Invert()
            .Then(new Affine(factor, 0, 0, factor, 0, 0))
            .Then(Frames.BoxToVisual(wanted.Bounds, wanted.Rotation).Then(page.VisualToPage));
        var still = Math.Abs(move.A - 1) < 1e-5 && Math.Abs(move.D - 1) < 1e-5 && Math.Abs(move.B) < 1e-5 && Math.Abs(move.C) < 1e-5 &&
                    Math.Abs(move.E) < 0.01 && Math.Abs(move.F) < 0.01;
        if (still) return changed;
        foreach (var handle in stored.Handles) Native.FPDFPageObj_Transform(handle, move.A, move.B, move.C, move.D, move.E, move.F);
        return true;
    }

    private static List<nint> CreateText(PdfPage page, DocFonts fonts, PageText text, int insertAt)
    {
        var layout = TextLayout.Create(text);
        var frame = Frames.BoxToVisual(text.Bounds, text.Rotation);
        var style = new TextStyle(text.FontSize, text.Color | 0xFF000000, text.FontFamily, text.Bold, text.Italic);
        var created = new List<nint>();
        (double X, double Y) origin = default;
        foreach (var line in layout.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var (x, y) = frame.Apply(line.X, line.Baseline);
            if (created.Count == 0) origin = (line.X, line.Baseline);
            created.AddRange(PageDraw.DrawText(page, fonts, line.Text, style, x, y, text.Rotation,
                insertAt: insertAt < 0 ? -1 : insertAt + created.Count));
        }

        for (var i = 0; i < created.Count; i++)
        {
            // Only what cannot be read back from the text objects themselves is written down, on the first of them.
            var definition = i > 0 ? null : new JsonObject
            {
                ["k"] = TextKind,
                ["t"] = text.Text,
                ["f"] = text.FontFamily,
                ["s"] = Rounded(text.FontSize),
                ["b"] = text.Bold ? 1 : 0,
                ["i"] = text.Italic ? 1 : 0,
                ["a"] = (int)text.Align,
                ["l"] = Rounded(text.LineSpacing),
                ["w"] = Rounded(text.Bounds.Width),
                ["h"] = Rounded(text.Bounds.Height),
                ["ox"] = Rounded(origin.X),
                ["oy"] = Rounded(origin.Y)
            };
            Tag(page.Doc, created[i], text.Id, definition);
        }

        return created;
    }

    private static bool Matches(Affine wanted, Affine actual)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-3 + 1e-5 * Math.Abs(a);
        return Near(wanted.A, actual.A) && Near(wanted.B, actual.B) && Near(wanted.C, actual.C) &&
               Near(wanted.D, actual.D) && Near(wanted.E, actual.E) && Near(wanted.F, actual.F);
    }

    private static JsonObject ImageDefinition(PageImage image) =>
        new() { ["k"] = ImageKind, ["o"] = Rounded(image.Opacity), ["v"] = image.Version };

    private static void Place(nint image, Affine m) => Native.FPDFImageObj_SetMatrix(image, m.A, m.B, m.C, m.D, m.E, m.F);

    private static void Insert(PdfPage page, nint obj, int insertAt)
    {
        if (insertAt < 0) Native.FPDFPage_InsertObject(page.Handle, obj);
        else if (Native.FPDFPage_InsertObjectAtIndex(page.Handle, obj, (nuint)insertAt) == 0)
            throw new PdfException("Could not put an object on the page.");
    }

    private static int IndexOf(PdfPage page, nint obj)
    {
        var count = page.ObjectCount;
        for (var i = 0; i < count; i++)
            if (Native.FPDFPage_GetObject(page.Handle, i) == obj)
                return i;
        return -1;
    }

    private static bool UpdateImage(PdfPage page, PageImage image, Stored existing, ref bool replaced)
    {
        var obj = existing.Head;
        var changed = false;
        var placement = UnitToPage(page, image);
        if (!Matches(placement, MatrixOf(obj)))
        {
            Place(obj, placement);
            changed = true;
        }

        var opacity = Math.Clamp(image.Opacity, 0.02, 1);
        var samePixels = (int)Number(existing.Definition, "v") == image.Version;
        if (!samePixels)
        {
            PageDraw.SetBitmap(page, obj, image.Pixels.Bitmap);
            replaced = true;
        }

        if (samePixels && Math.Abs(Number(existing.Definition, "o", 1) - opacity) <= 0.001) return changed;
        Native.FPDFPageObj_SetFillColor(obj, 0, 0, 0, (uint)Math.Round(opacity * 255));
        WriteDefinition(page.Doc, obj, existing.HeadMark, ImageDefinition(image with { Opacity = opacity }));
        return true;
    }

    private static nint CreateImage(PdfPage page, PageImage image, int insertAt, byte[]? storedJpeg)
    {
        var created = Native.FPDFPageObj_NewImageObj(page.Doc.Handle);
        if (created == 0) throw new PdfException("Could not create an image object.");
        var pixels = image.Pixels;
        if ((storedJpeg ?? pixels.Jpeg) is { } jpeg) PageDraw.SetJpeg(page, created, jpeg);
        else if (pixels.HasAlpha || pixels.Lossless) PageDraw.SetBitmap(page, created, pixels.Bitmap);
        else PageDraw.SetJpeg(page, created, ImageFilters.EncodeJpeg(pixels.Bitmap, 92));
        Place(created, UnitToPage(page, image));
        // Opacity is a property of how the picture is painted, not of its pixels: it can be changed later without loss.
        var opacity = Math.Clamp(image.Opacity, 0.02, 1);
        Native.FPDFPageObj_SetFillColor(created, 0, 0, 0, (uint)Math.Round(opacity * 255));
        Tag(page.Doc, created, image.Id, ImageDefinition(image with { Opacity = opacity }));
        Insert(page, created, insertAt);
        return created;
    }

    /// <summary>The stored bytes of an image when they are a complete JPEG file, which can be reused as it is.</summary>
    private static byte[]? RawJpeg(nint image)
    {
        if (Native.FPDFImageObj_GetImageFilterCount(image) != 1) return null;
        var name = stackalloc byte[32];
        var length = (int)Native.FPDFImageObj_GetImageFilter(image, 0, name, new CULong(32)).Value;
        if (length is <= 1 or > 32 || Encoding.ASCII.GetString(name, length - 1) != "DCTDecode") return null;
        var size = (int)Native.FPDFImageObj_GetImageDataRaw(image, null, new CULong(0)).Value;
        if (size <= 0) return null;
        var data = new byte[size];
        fixed (byte* p = data) Native.FPDFImageObj_GetImageDataRaw(image, p, new CULong((uint)size));
        return data;
    }

    private static bool UpdatePatch(PdfPage page, PagePatch patch, Stored existing)
    {
        var obj = existing.Head;
        var changed = false;
        var placement = UnitToPage(page, patch);
        if (!Matches(placement, MatrixOf(obj)))
        {
            var m = new FS_MATRIX
            {
                a = (float)placement.A, b = (float)placement.B, c = (float)placement.C,
                d = (float)placement.D, e = (float)placement.E, f = (float)placement.F
            };
            Native.FPDFPageObj_SetMatrix(obj, &m);
            changed = true;
        }

        var color = patch.Color | 0xFF000000;
        if ((PageDraw.GetFill(obj) | 0xFF000000) == color) return changed;
        PageDraw.SetFill(obj, color);
        return true;
    }

    private static nint CreatePatch(PdfPage page, PagePatch patch, int insertAt)
    {
        var placement = UnitToPage(page, patch);
        var created = Native.FPDFPageObj_CreateNewRect(0, 0, 1, 1);
        if (created == 0) throw new PdfException("Could not create a rectangle.");
        PageDraw.SetFill(created, patch.Color | 0xFF000000);
        Native.FPDFPath_SetDrawMode(created, Native.FPDF_FILLMODE_ALTERNATE, 0);
        Native.FPDFPageObj_Transform(created, placement.A, placement.B, placement.C, placement.D, placement.E, placement.F);
        Tag(page.Doc, created, patch.Id, new JsonObject { ["k"] = PatchKind });
        Insert(page, created, insertAt);
        return created;
    }

    /// <summary>Positions in <paramref name="values"/> of a longest run of rising numbers.</summary>
    private static HashSet<int> LongestRising(List<int> values)
    {
        var length = new int[values.Count];
        var previous = new int[values.Count];
        var best = -1;
        for (var i = 0; i < values.Count; i++)
        {
            length[i] = 1;
            previous[i] = -1;
            for (var j = 0; j < i; j++)
                if (values[j] < values[i] && length[j] + 1 > length[i])
                    (length[i], previous[i]) = (length[j] + 1, j);
            if (best < 0 || length[i] > length[best]) best = i;
        }

        var run = new HashSet<int>();
        for (var i = best; i >= 0; i = previous[i]) run.Add(i);
        return run;
    }

    private static bool EraseTextUnder(PdfPage page, RectD area)
    {
        var removed = false;
        foreach (var obj in page.GetObjects())
        {
            if (Native.FPDFPageObj_GetType(obj) != Native.FPDF_PAGEOBJ_TEXT || IsEditable(obj)) continue;
            var b = page.GetVisualBounds(obj);
            // Text nobody sees (the layer behind a scan) goes when its middle is covered; visible text only when all of it is.
            var hidden = Native.FPDFTextObj_GetTextRenderMode(obj) == Native.FPDF_TEXTRENDERMODE_INVISIBLE;
            var inside = hidden
                ? area.Contains(b.CenterX, b.CenterY)
                : b.Left >= area.Left - 1 && b.Right <= area.Right + 1 && b.Top >= area.Top - 1 && b.Bottom <= area.Bottom + 1;
            if (!inside) continue;
            page.RemoveObject(obj);
            removed = true;
        }

        return removed;
    }
}
