using System.Runtime.InteropServices;
using PdfModifier.Core.Pdfium;

namespace PdfModifier.Core.Operations;

public readonly record struct PageInfo(double Width, double Height, int RotationDegrees);

public enum ScaleMode
{
    /// <summary>Multiply the page size and its content by a factor.</summary>
    Factor,

    /// <summary>Resize to a target page size, scaling the content to fit and centring it.</summary>
    FitToSize
}

/// <summary>Structural page operations. Every method takes a document as bytes and returns a new document.</summary>
public static unsafe class PdfPages
{
    public static IReadOnlyList<PageInfo> GetPageInfos(byte[] pdf)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            var result = new PageInfo[doc.PageCount];
            for (var i = 0; i < result.Length; i++)
            {
                using var page = doc.LoadPage(i);
                result[i] = new PageInfo(page.Width, page.Height, page.Rotation * 90);
            }

            return result;
        }
    }

    public static int CountPages(byte[] pdf)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            return doc.PageCount;
        }
    }

    /// <summary>The page each top-level bookmark jumps to (-1 when it has no page destination).</summary>
    public static List<int> GetBookmarkTargets(byte[] pdf)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            var targets = new List<int>();
            for (var bookmark = Native.FPDFBookmark_GetFirstChild(doc.Handle, 0);
                 bookmark != 0 && targets.Count < 100_000;
                 bookmark = Native.FPDFBookmark_GetNextSibling(doc.Handle, bookmark))
            {
                var dest = Native.FPDFBookmark_GetDest(doc.Handle, bookmark);
                targets.Add(dest == 0 ? -1 : Native.FPDFDest_GetDestPageIndex(doc.Handle, dest));
            }

            return targets;
        }
    }

    /// <summary>Removes the open password/encryption so the rest of the pipeline can work on plain bytes.</summary>
    public static byte[] Unlock(byte[] pdf, string? password)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf, password);
            return doc.Save(removeSecurity: true);
        }
    }

    public static bool NeedsPassword(byte[] pdf)
    {
        lock (PdfDoc.Sync)
        {
            try
            {
                using var doc = PdfDoc.Load(pdf);
                return false;
            }
            catch (PdfPasswordException)
            {
                return true;
            }
        }
    }

    /// <summary>Concatenates documents. A null page list takes every page of that source.</summary>
    public static byte[] Merge(IReadOnlyList<(byte[] Pdf, IReadOnlyList<int>? Pages)> sources)
    {
        if (sources.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(sources));
        lock (PdfDoc.Sync)
        {
            using var target = PdfDoc.CreateNew();
            foreach (var (pdf, pages) in sources)
            {
                using var source = PdfDoc.Load(pdf);
                if (target.PageCount == 0) Native.FPDF_CopyViewerPreferences(target.Handle, source.Handle);
                target.ImportPages(source, pages, target.PageCount);
            }

            return target.Save();
        }
    }

    public static byte[] Merge(IReadOnlyList<byte[]> pdfs) =>
        Merge(pdfs.Select(p => (p, (IReadOnlyList<int>?)null)).ToList());

    /// <summary>Builds a new document from the given pages, in the given order.</summary>
    public static byte[] Extract(byte[] pdf, IReadOnlyList<int> pages)
    {
        lock (PdfDoc.Sync)
        {
            using var source = PdfDoc.Load(pdf);
            ValidatePages(pages, source.PageCount);
            using var target = PdfDoc.CreateNew();
            Native.FPDF_CopyViewerPreferences(target.Handle, source.Handle);
            target.ImportPages(source, pages, 0);
            return target.Save();
        }
    }

    public static List<byte[]> Split(byte[] pdf, IReadOnlyList<IReadOnlyList<int>> groups)
    {
        var result = new List<byte[]>(groups.Count);
        foreach (var group in groups) result.Add(Extract(pdf, group));
        return result;
    }

    /// <summary>Page groups for "one file every N pages".</summary>
    public static List<List<int>> GroupsOf(int pageCount, int pagesPerFile)
    {
        if (pagesPerFile < 1) throw new ArgumentOutOfRangeException(nameof(pagesPerFile));
        var groups = new List<List<int>>();
        for (var start = 0; start < pageCount; start += pagesPerFile)
            groups.Add(Enumerable.Range(start, Math.Min(pagesPerFile, pageCount - start)).ToList());
        return groups;
    }

    public static byte[] Delete(byte[] pdf, IReadOnlyCollection<int> pages)
    {
        byte[] saved;
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            ValidatePages(pages, doc.PageCount);
            var distinct = pages.Distinct().OrderByDescending(p => p).ToList();
            if (distinct.Count >= doc.PageCount) throw new PdfException("A document needs at least one page.");
            foreach (var p in distinct) Native.FPDFPage_Delete(doc.Handle, p);
            saved = doc.Save();
        }

        // PDFium keeps the deleted pages' objects in the file; drop them so the content is really gone.
        return Sharp.Compact(saved);
    }

    /// <summary>Rotates pages clockwise by a multiple of 90 degrees, relative to their current rotation.</summary>
    public static byte[] Rotate(byte[] pdf, IReadOnlyCollection<int>? pages, int degrees)
    {
        if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(degrees));
        return ForEachPage(pdf, pages, page => page.Rotation += degrees / 90);
    }

    /// <summary>
    /// Mirrors pages as they are shown: left to right, as a mirror standing beside the page would, or with
    /// <paramref name="topToBottom"/> as one lying under it. For scans that were made from the wrong side of a
    /// transparency or a tracing, and photographs taken in a mirror.
    /// </summary>
    public static byte[] Mirror(byte[] pdf, IReadOnlyCollection<int>? pages, bool topToBottom = false) =>
        ForEachPage(pdf, pages, page =>
        {
            var (l, b, r, t) = page.GetUserBox();
            // On a page that is shown turned on its side, left and right are bottom and top in its own coordinates.
            var acrossIsX = page.Rotation % 2 == 0;
            var matrix = acrossIsX != topToBottom
                ? new FS_MATRIX { a = -1, d = 1, e = (float)(l + r) }
                : new FS_MATRIX { a = 1, d = -1, f = (float)(b + t) };
            var clip = new FS_RECTF { left = (float)l, bottom = (float)b, right = (float)r, top = (float)t };
            if (Native.FPDFPage_TransFormWithClip(page.Handle, &matrix, &clip) == 0)
                throw new PdfException($"Could not mirror page {page.Index + 1}.");
            Native.FPDFPage_TransformAnnots(page.Handle, matrix.a, 0, 0, matrix.d, matrix.e, matrix.f);
        });

    /// <summary>Reorders pages; <paramref name="newOrder"/>[i] is the current index of the page that ends up at position i.</summary>
    public static byte[] Reorder(byte[] pdf, IReadOnlyList<int> newOrder)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            var count = doc.PageCount;
            if (newOrder.Count != count || newOrder.Distinct().Count() != count || newOrder.Any(p => p < 0 || p >= count))
                throw new ArgumentException("The new order must list every page exactly once.", nameof(newOrder));
            var order = newOrder.ToArray();
            fixed (int* p = order)
            {
                if (Native.FPDF_MovePages(doc.Handle, p, new CULong((uint)count), 0) == 0)
                    throw new PdfException("Could not reorder the pages.");
            }

            return doc.Save();
        }
    }

    public static byte[] InsertBlank(byte[] pdf, int atIndex, double width, double height, int count = 1)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            atIndex = Math.Clamp(atIndex, 0, doc.PageCount);
            for (var i = 0; i < count; i++)
            {
                using var page = doc.NewPage(atIndex + i, width, height);
                page.GenerateContent();
            }

            return doc.Save();
        }
    }

    /// <summary>Inserts pages of <paramref name="other"/> before the page at <paramref name="atIndex"/>.</summary>
    public static byte[] InsertPdf(byte[] pdf, byte[] other, IReadOnlyList<int>? otherPages, int atIndex)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var source = PdfDoc.Load(other);
            if (otherPages != null) ValidatePages(otherPages, source.PageCount);
            doc.ImportPages(source, otherPages, Math.Clamp(atIndex, 0, doc.PageCount));
            return doc.Save();
        }
    }

    /// <summary>
    /// Scales pages together with their content. With <see cref="ScaleMode.Factor"/> only <paramref name="factor"/> is used;
    /// with <see cref="ScaleMode.FitToSize"/> the page becomes <paramref name="targetWidth"/> × <paramref name="targetHeight"/> points
    /// (as displayed) and the content is scaled to fit, keeping its aspect ratio.
    /// </summary>
    public static byte[] Scale(byte[] pdf, IReadOnlyCollection<int>? pages, ScaleMode mode, double factor = 1,
        double targetWidth = 0, double targetHeight = 0)
    {
        if (mode == ScaleMode.Factor && factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (mode == ScaleMode.FitToSize && (targetWidth <= 0 || targetHeight <= 0))
            throw new ArgumentException("A target size is required.");

        return ForEachPage(pdf, pages, page =>
        {
            var (l, b, r, t) = page.GetUserBox();
            double w = r - l, h = t - b;
            double newW, newH, s;
            if (mode == ScaleMode.Factor)
            {
                s = factor;
                newW = w * s;
                newH = h * s;
            }
            else
            {
                // The target is given as displayed; user space is unrotated.
                (newW, newH) = page.Rotation % 2 == 0 ? (targetWidth, targetHeight) : (targetHeight, targetWidth);
                s = Math.Min(newW / w, newH / h);
            }

            var dx = (newW - w * s) / 2 - l * s;
            var dy = (newH - h * s) / 2 - b * s;
            var matrix = new FS_MATRIX { a = (float)s, d = (float)s, e = (float)dx, f = (float)dy };
            var clip = new FS_RECTF { left = 0, bottom = 0, right = (float)newW, top = (float)newH };
            if (Native.FPDFPage_TransFormWithClip(page.Handle, &matrix, &clip) == 0)
                throw new PdfException($"Could not scale page {page.Index + 1}.");
            Native.FPDFPage_TransformAnnots(page.Handle, s, 0, 0, s, dx, dy);
            Native.FPDFPage_SetMediaBox(page.Handle, 0, 0, (float)newW, (float)newH);
            Native.FPDFPage_SetCropBox(page.Handle, 0, 0, (float)newW, (float)newH);
        });
    }

    /// <summary>Crops pages to a rectangle given in visual coordinates (points from the top-left of the page as displayed).</summary>
    public static byte[] Crop(byte[] pdf, IReadOnlyCollection<int>? pages, Func<PageInfo, RectD> rectFor) =>
        ForEachPage(pdf, pages, page =>
        {
            var info = new PageInfo(page.Width, page.Height, page.Rotation * 90);
            ApplyCrop(page, rectFor(info));
        });

    /// <summary>Crops by trimming margins (points) from each visual edge.</summary>
    public static byte[] CropMargins(byte[] pdf, IReadOnlyCollection<int>? pages, double left, double top, double right, double bottom) =>
        Crop(pdf, pages, info => new RectD(left, top, info.Width - right, info.Height - bottom));

    /// <summary>Crops each page to the bounding box of its visible content plus <paramref name="padding"/> points.</summary>
    public static byte[] AutoCrop(byte[] pdf, IReadOnlyCollection<int>? pages, double padding = 6) =>
        ForEachPage(pdf, pages, page =>
        {
            if (ContentArea(page, padding) is { } area) ApplyCrop(page, area);
        });

    /// <summary>
    /// The area <see cref="AutoCrop"/> would keep of a page, in visual coordinates; null for a blank page, which is
    /// left as it is.
    /// </summary>
    public static RectD? AutoCropArea(byte[] pdf, int pageIndex, double padding = 6)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            using var page = doc.LoadPage(pageIndex);
            return ContentArea(page, padding);
        }
    }

    private static RectD? ContentArea(PdfPage page, double padding)
    {
        const double scale = 1.5;
        using var bitmap = page.Render(scale);
        if (Imaging.ImageAnalysis.FindContentBounds(bitmap) is not { } c) return null;
        return new RectD(
            Math.Max(0, c.Left / scale - padding), Math.Max(0, c.Top / scale - padding),
            Math.Min(page.Width, c.Right / scale + padding), Math.Min(page.Height, c.Bottom / scale + padding));
    }

    /// <summary>Merges annotations and form fields into the page content so they can no longer be edited.</summary>
    public static byte[] Flatten(byte[] pdf, IReadOnlyCollection<int>? pages) =>
        ForEachPage(pdf, pages, page =>
        {
            if (Native.FPDFPage_Flatten(page.Handle, Native.FLAT_NORMALDISPLAY) == Native.FLATTEN_FAIL)
                throw new PdfException($"Could not flatten page {page.Index + 1}.");
        });

    private static void ApplyCrop(PdfPage page, RectD visual)
    {
        var left = Math.Clamp(visual.Left, 0, page.Width);
        var right = Math.Clamp(visual.Right, 0, page.Width);
        var top = Math.Clamp(visual.Top, 0, page.Height);
        var bottom = Math.Clamp(visual.Bottom, 0, page.Height);
        if (right - left < 1 || bottom - top < 1) throw new PdfException("The crop area is empty.");
        var m = page.VisualToPage;
        var (x0, y0) = m.Apply(left, top);
        var (x1, y1) = m.Apply(right, bottom);
        Native.FPDFPage_SetCropBox(page.Handle,
            (float)Math.Min(x0, x1), (float)Math.Min(y0, y1), (float)Math.Max(x0, x1), (float)Math.Max(y0, y1));
    }

    internal static byte[] ForEachPage(byte[] pdf, IReadOnlyCollection<int>? pages, Action<PdfPage> action)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            foreach (var index in ResolvePages(pages, doc.PageCount))
            {
                using var page = doc.LoadPage(index);
                action(page);
            }

            return doc.Save();
        }
    }

    internal static List<int> ResolvePages(IReadOnlyCollection<int>? pages, int pageCount)
    {
        if (pages == null) return Enumerable.Range(0, pageCount).ToList();
        ValidatePages(pages, pageCount);
        return pages.Distinct().OrderBy(p => p).ToList();
    }

    private static void ValidatePages(IReadOnlyCollection<int> pages, int pageCount)
    {
        foreach (var p in pages)
            if (p < 0 || p >= pageCount)
                throw new ArgumentOutOfRangeException(nameof(pages), $"Page {p + 1} does not exist.");
    }
}
