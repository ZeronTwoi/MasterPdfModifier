using PdfModifier.Core.Imaging;
using PdfModifier.Core.Pdfium;
using SkiaSharp;

namespace PdfModifier.Core.Operations;

/// <summary>Clean-up steps for scanned pages, plus darkening of faint text on text-based pages.</summary>
public sealed record EnhanceOptions
{
    /// <summary>Straighten pages that were scanned at a slight angle.</summary>
    public bool Deskew { get; init; } = true;

    /// <summary>Turn the paper white and the ink black.</summary>
    public bool WhitenBackground { get; init; } = true;

    /// <summary>0-1. Higher values also wipe out faint marks such as light watermarks baked into a scan.</summary>
    public double WhitenStrength { get; init; } = 0.3;

    public bool Sharpen { get; init; } = true;
    public bool Denoise { get; init; }

    /// <summary>Convert scans to pure black and white. Smallest and crispest for text, unsuitable for photos.</summary>
    public bool Binarize { get; init; }

    public bool Grayscale { get; init; }

    /// <summary>On text-based pages, make light gray text black.</summary>
    public bool DarkenText { get; init; } = true;

    /// <summary>0-1. Brings print that is out of focus back into it; 0 leaves it alone.</summary>
    public double Deblur { get; init; }

    /// <summary>0-1. Makes thin, pale or broken strokes heavier; 0 leaves them alone.</summary>
    public double Thicken { get; init; }

    /// <summary>Even out shadows and uneven light before anything else, as on a photograph of a page.</summary>
    public bool EvenLight { get; init; }

    /// <summary>
    /// Double the number of pixels of scans below about 200 dpi before working on them. Small print then has enough
    /// pixels for the other steps to shape; the picture takes more room in the file.
    /// </summary>
    public bool Upscale { get; init; }
}

public sealed record EnhanceResult(byte[] Pdf, int ImagesEnhanced, int TextObjectsDarkened);

public static unsafe class PdfEnhance
{
    public static EnhanceResult Enhance(byte[] pdf, IReadOnlyCollection<int>? pages = null, EnhanceOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        options ??= new EnhanceOptions();
        // Counted per page: the editor may run a page a second time when it has to fall back to another strategy.
        var counts = new Dictionary<int, (int Images, int Texts)>();
        var result = ContentEditor.Run(pdf, pages, (page, index, _) =>
        {
            var images = EnhancePictures(page, options);
            var texts = 0;
            if (options.DarkenText)
                foreach (var obj in page.GetObjects())
                    if (Native.FPDFPageObj_GetType(obj) == Native.FPDF_PAGEOBJ_TEXT && DarkenText(obj))
                        texts++;

            counts[index] = (images, texts);
            return images + texts > 0;
        }, null, progress, cancel);

        int totalImages = counts.Values.Sum(c => c.Images), totalTexts = counts.Values.Sum(c => c.Texts);
        if (totalImages + totalTexts == 0) return new EnhanceResult(pdf, 0, 0);
        return new EnhanceResult(totalImages > 0 ? ImageObjects.DropReplacedImages(result) : result, totalImages, totalTexts);
    }

    /// <summary>Below this resolution a scan is taken to be too coarse for small print (see <see cref="EnhanceOptions.Upscale"/>).</summary>
    private const double CoarseDpi = 210;

    /// <summary>
    /// Runs the image clean-up on a single bitmap. Returns a Gray8 or Bgra8888 bitmap owned by the caller. With
    /// <see cref="EnhanceOptions.Upscale"/> the result has twice the width and height.
    /// </summary>
    public static SKBitmap EnhanceBitmap(SKBitmap source, EnhanceOptions options) => EnhanceBitmap(source, options, null, options.Upscale);

    /// <param name="levels">Black and white points to use instead of the image's own, for pieces of one page that must match.</param>
    /// <param name="enlarge">Double the pixels first.</param>
    private static SKBitmap EnhanceBitmap(SKBitmap source, EnhanceOptions options, (int Black, int White)? levels, bool enlarge = false)
    {
        using var larger = enlarge && (long)source.Width * source.Height <= 12_000_000
            ? ImageFilters.Resize(source, source.Width * 2, source.Height * 2)
            : null;
        var image = PlanarImage.FromBitmap(larger ?? source, options.Grayscale || options.Binarize);

        if (options.Deskew)
        {
            var skew = ImageFilters.EstimateSkew(image);
            if (skew != 0)
            {
                using var unrotated = image.ToBitmap();
                using var rotated = ImageFilters.Rotate(unrotated, -skew);
                image = PlanarImage.FromBitmap(rotated, image.IsGray);
            }
        }

        if (options.EvenLight) ImageFilters.EvenLight(image);
        if (options.Denoise) ImageFilters.Denoise(image);
        // Focus before the levels are stretched: stretching first would cut off the soft edges focusing works from.
        if (options.Deblur > 0) ImageFilters.Deblur(image, options.Deblur);
        if (options.WhitenBackground)
        {
            if (levels is var (black, white)) ImageFilters.ApplyLevels(image, black, white);
            else ImageFilters.WhitenBackground(image, options.WhitenStrength);
        }

        if (options.Thicken > 0) ImageFilters.Thicken(image, options.Thicken);
        // Print that was just brought into focus has its edges already; sharpening on top would ring them with halos.
        if (options.Sharpen && !options.Binarize && options.Deblur <= 0) ImageFilters.Sharpen(image);
        if (options.Binarize) ImageFilters.Binarize(image);
        return image.ToBitmap();
    }

    /// <summary>
    /// Cleans up the pictures that make up a scanned page. Usually that is one image filling the page; some scanners
    /// store a page as several strips or tiles instead, which are then treated as the one picture they form.
    /// Logos and figures on text pages are left alone.
    /// </summary>
    private static int EnhancePictures(PdfPage page, EnhanceOptions options)
    {
        double pageArea = Math.Max(1, page.Width * page.Height);
        var whole = new List<nint>();
        var pieces = new List<(nint Obj, double Share)>();
        foreach (var obj in page.GetObjects())
        {
            if (Native.FPDFPageObj_GetType(obj) != Native.FPDF_PAGEOBJ_IMAGE || PdfPageObjects.IsEditable(obj)) continue;
            if (!ImageObjects.IsRecompressible(page, obj, out var meta, out _)) continue;
            var bounds = page.GetVisualBounds(obj);
            var share = bounds.Width * bounds.Height / pageArea;
            if (meta.width >= 300 && meta.height >= 300 && share >= 0.25) whole.Add(obj);
            else if (meta.width >= 64 && meta.height >= 64) pieces.Add((obj, share));
        }

        var count = 0;
        foreach (var obj in whole)
        {
            using var source = ImageObjects.GetPixels(obj);
            if (source == null) continue;
            var bounds = page.GetVisualBounds(obj);
            var dpi = source.Width / Math.Max(1e-6, bounds.Width / 72);
            using var enhanced = EnhanceBitmap(source, options, null, options.Upscale && dpi < CoarseDpi);
            Store(page, obj, enhanced, options);
            count++;
        }

        if (whole.Count > 0 || pieces.Count < 2 || pieces.Sum(p => p.Share) < 0.6) return count;

        var bitmaps = pieces.Select(p => (p.Obj, Pixels: ImageObjects.GetPixels(p.Obj))).Where(p => p.Pixels != null).ToList();
        try
        {
            // One set of levels for the whole page, or neighbouring strips would come out in different shades. A strip
            // cannot be straightened on its own either: it would no longer line up with the next one.
            (int, int)? levels = null;
            if (options.WhitenBackground)
            {
                var histogram = new long[256];
                foreach (var (_, pixels) in bitmaps)
                    foreach (var v in PlanarImage.FromBitmap(pixels!, true).Planes[0])
                        histogram[v]++;
                levels = ImageFilters.EstimateLevels(histogram, options.WhitenStrength);
            }

            // Nor is a strip lit or focused on its own: what is done to one has to match its neighbours.
            var together = options with { Deskew = false, EvenLight = false, WhitenBackground = options.WhitenBackground && levels != null };
            foreach (var (obj, pixels) in bitmaps)
            {
                using var enhanced = EnhanceBitmap(pixels!, together, levels);
                Store(page, obj, enhanced, options);
                count++;
            }
        }
        finally
        {
            foreach (var (_, pixels) in bitmaps) pixels?.Dispose();
        }

        return count;
    }

    private static void Store(PdfPage page, nint obj, SKBitmap enhanced, EnhanceOptions options)
    {
        // Two-level results compress far better losslessly; everything else goes back as JPEG.
        if (options.Binarize) PageDraw.SetBitmap(page, obj, enhanced);
        else PageDraw.SetJpeg(page, obj, ImageFilters.EncodeJpeg(enhanced, 88));
    }

    private static bool DarkenText(nint obj)
    {
        if (Native.FPDFTextObj_GetTextRenderMode(obj) == Native.FPDF_TEXTRENDERMODE_INVISIBLE) return false;
        var argb = PageDraw.GetFill(obj);
        uint r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF, a = argb >> 24;
        var luma = (r * 299 + g * 587 + b * 114) / 1000;
        // Faint gray, but not the white of reversed-out text, and only near-neutral so coloured text keeps its colour.
        var neutral = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 40;
        if (!neutral || luma is < 70 or > 215 || a < 128) return false;
        return Native.FPDFPageObj_SetFillColor(obj, 0, 0, 0, a) != 0;
    }
}
