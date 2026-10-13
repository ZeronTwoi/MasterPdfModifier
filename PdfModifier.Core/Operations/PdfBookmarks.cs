using System.Runtime.InteropServices;
using System.Text;
using PdfModifier.Core.Pdfium;
using PdfSharp.Pdf;

namespace PdfModifier.Core.Operations;

/// <summary>
/// An entry of a document's outline, the list of contents a reader shows beside the pages: what it says, the page
/// it leads to (from 0; -1 for an entry that leads to no page), how deep it stands under the entries before it
/// (0 for the outermost), how far down that page it leads (points from the top of the page as shown; null for the
/// top), and whether the entries under it are on view.
/// </summary>
public sealed record Bookmark(string Title, int Page, int Level = 0, double? Top = null, bool Open = true);

/// <summary>A bookmark the app proposes, and whether it is a close call that someone should look at.</summary>
public sealed record SuggestedBookmark(Bookmark Bookmark, bool Doubtful);

/// <summary>Reads, writes and proposes the bookmarks of a document.</summary>
public static unsafe class PdfBookmarks
{
    /// <summary>How readily a line counts as a heading when nothing else is said: halfway between only the largest print and nearly anything larger than the text.</summary>
    public const double UsualReadiness = 0.5;

    private const int Most = 20000, Deepest = 24;

    /// <summary>The bookmarks of a document, in the order a reader lists them.</summary>
    public static IReadOnlyList<Bookmark> Read(byte[] pdf)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            var found = new List<Bookmark>();
            var seen = new HashSet<nint>();
            var tops = new Dictionary<int, Affine>();
            Walk(Native.FPDFBookmark_GetFirstChild(doc.Handle, 0), 0);
            return found;

            void Walk(nint bookmark, int level)
            {
                // A damaged outline may lead round in a circle; every entry is taken once.
                for (; bookmark != 0 && found.Count < Most && seen.Add(bookmark); bookmark = Native.FPDFBookmark_GetNextSibling(doc.Handle, bookmark))
                {
                    var dest = Native.FPDFBookmark_GetDest(doc.Handle, bookmark);
                    if (dest == 0 && Native.FPDFBookmark_GetAction(bookmark) is not 0 and var action) dest = Native.FPDFAction_GetDest(doc.Handle, action);
                    var page = dest == 0 ? -1 : Native.FPDFDest_GetDestPageIndex(doc.Handle, dest);
                    if (page >= doc.PageCount) page = -1;
                    found.Add(new Bookmark(Title(bookmark), page, level, page < 0 ? null : Top(dest, page), Native.FPDFBookmark_GetCount(bookmark) >= 0));
                    if (level < Deepest) Walk(Native.FPDFBookmark_GetFirstChild(doc.Handle, bookmark), level + 1);
                }
            }

            // How far down the page as it is shown the entry leads; the file says it in the page's own coordinates.
            double? Top(nint dest, int page)
            {
                int hasX, hasY, hasZoom;
                float x, y, zoom;
                if (Native.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) == 0 || hasY == 0) return null;
                if (!tops.TryGetValue(page, out var toVisual))
                {
                    using var loaded = doc.LoadPage(page);
                    tops[page] = toVisual = loaded.PageToVisual;
                }

                var (width, height) = doc.GetPageSize(page);
                // Without a place across the page, the middle of it stands in: on an upright page that changes nothing.
                var down = toVisual.Apply(hasX != 0 ? x : 0, y).Y;
                return down <= 0.5 || down >= height - 0.5 || double.IsNaN(down) || width <= 0 ? null : down;
            }
        }
    }

    private static string Title(nint bookmark)
    {
        var bytes = (int)Native.FPDFBookmark_GetTitle(bookmark, null, new CULong(0)).Value;
        if (bytes <= 2) return "";
        var buffer = new byte[bytes];
        fixed (byte* p = buffer) Native.FPDFBookmark_GetTitle(bookmark, p, new CULong((uint)bytes));
        return Encoding.Unicode.GetString(buffer, 0, bytes - 2).Trim();
    }

    /// <summary>
    /// Gives a document these bookmarks in place of the ones it has; none takes them all away. An entry stands under
    /// the nearest one before it that is less deep; one that would stand deeper than directly under its predecessor
    /// is moved up to there.
    /// </summary>
    public static byte[] Write(byte[] pdf, IReadOnlyList<Bookmark> bookmarks) => Sharp.Rewrite(pdf, doc =>
    {
        var catalog = doc.Internals.Catalog;
        catalog.Elements.Remove("/Outlines");
        if (bookmarks.Count == 0)
        {
            if (catalog.Elements.GetName("/PageMode") == "/UseOutlines") catalog.Elements.Remove("/PageMode");
            return;
        }

        var levels = Levels(bookmarks);
        var root = new PdfDictionary(doc);
        doc.Internals.AddObject(root);
        root.Elements.SetName("/Type", "/Outlines");
        var items = bookmarks.Select(_ =>
        {
            var item = new PdfDictionary(doc);
            doc.Internals.AddObject(item);
            return item;
        }).ToList();

        // Who stands under whom, in order: -1 is the outline itself.
        var children = new Dictionary<int, List<int>> { [-1] = [] };
        var open = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            while (open.Count > levels[i]) open.RemoveAt(open.Count - 1);
            var parent = open.Count == 0 ? -1 : open[^1];
            (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(i);
            open.Add(i);

            var bookmark = bookmarks[i];
            items[i].Elements["/Title"] = new PdfString(bookmark.Title.Length == 0 ? " " : bookmark.Title, PdfStringEncoding.Unicode);
            items[i].Elements["/Parent"] = (parent < 0 ? root : items[parent]).Reference!;
            if (bookmark.Page >= 0 && bookmark.Page < doc.PageCount) items[i].Elements["/Dest"] = Destination(doc, doc.Pages[bookmark.Page], bookmark.Top);
        }

        foreach (var (parent, list) in children)
        {
            var owner = parent < 0 ? root : items[parent];
            owner.Elements["/First"] = items[list[0]].Reference!;
            owner.Elements["/Last"] = items[list[^1]].Reference!;
            for (var n = 0; n < list.Count; n++)
            {
                if (n > 0) items[list[n]].Elements["/Prev"] = items[list[n - 1]].Reference!;
                if (n + 1 < list.Count) items[list[n]].Elements["/Next"] = items[list[n + 1]].Reference!;
            }

            // How many entries show under this one: its own, and those under each of them that is open. A closed
            // entry says the same number with a minus in front.
            var showing = Showing(parent);
            owner.Elements.SetInteger("/Count", parent < 0 || bookmarks[parent].Open ? showing : -showing);
        }

        catalog.Elements["/Outlines"] = root.Reference!;

        int Showing(int parent) => children.TryGetValue(parent, out var list)
            ? list.Sum(child => 1 + (bookmarks[child].Open ? Showing(child) : 0))
            : 0;
    });

    /// <summary>How deep each entry really stands: the first outermost, none more than one step under its predecessor.</summary>
    public static List<int> Levels(IReadOnlyList<Bookmark> bookmarks)
    {
        var levels = new List<int>(bookmarks.Count);
        foreach (var bookmark in bookmarks) levels.Add(Math.Clamp(bookmark.Level, 0, levels.Count == 0 ? 0 : levels[^1] + 1));
        return levels;
    }

    /// <summary>
    /// Where an entry leads: the page, and how far down it. A reader keeps the enlargement it has; without a place
    /// on the page it shows the page from its top.
    /// </summary>
    private static PdfArray Destination(PdfDocument doc, PdfSharp.Pdf.PdfPage page, double? top)
    {
        var where = new PdfArray(doc);
        where.Elements.Add(page.Reference!);
        where.Elements.Add(new PdfName("/XYZ"));
        if (top is not { } down)
        {
            where.Elements.Add(PdfNull.Value);
            where.Elements.Add(PdfNull.Value);
        }
        else
        {
            // From "points down the page as shown" back to the page's own coordinates, whichever way it is turned.
            var box = page.Elements.GetRectangle("/CropBox") is { IsZero: false } crop ? crop : page.MediaBox;
            var turn = ((page.Elements.GetInteger("/Rotate") / 90 % 4) + 4) % 4;
            var (x, y) = turn switch
            {
                1 => (box.X1 + down, box.Y1),
                2 => (box.X2, box.Y1 + down),
                3 => (box.X2 - down, box.Y2),
                _ => (box.X1, box.Y2 - down)
            };
            where.Elements.Add(new PdfReal(x));
            where.Elements.Add(new PdfReal(y));
        }

        where.Elements.Add(PdfNull.Value);
        return where;
    }

    // ---- proposing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Proposes bookmarks from the headings of a document: the lines printed larger than its running text. With a
    /// high <paramref name="readiness"/> (0 to 1) a line a little larger counts already, with a low one only what
    /// stands out clearly. Headings that are a close call are marked as doubtful; pages that are pictures of text
    /// have no print to measure and are counted in <paramref name="unread"/>.
    /// </summary>
    public static IReadOnlyList<SuggestedBookmark> Suggest(PdfView view, double readiness, out int unread, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        unread = 0;
        readiness = Math.Clamp(readiness, 0, 1);
        var lines = new List<(int Page, PrintedLine Printed)>();
        for (var page = 0; page < view.PageCount; page++)
        {
            cancel.ThrowIfCancellationRequested();
            var onPage = view.GetPrintedLines(page);
            if (onPage.Count == 0 && PdfText.IsPicture(view, page)) unread++;
            foreach (var printed in onPage)
                if (printed.Size > 2 && printed.Line.Bounds.Width >= printed.Line.Bounds.Height)
                    lines.Add((page, printed));
            progress?.Report((page + 1.0) / view.PageCount);
        }

        if (lines.Count == 0) return [];

        // The print of the running text: the size most letters are set in, and whether that text is bold itself.
        var running = lines.GroupBy(entry => entry.Printed.Size).MaxBy(group => group.Sum(entry => entry.Printed.Line.Text.Length))!;
        var body = running.Key;
        var bodyIsBold = running.Count(entry => entry.Printed.Bold) > running.Count() / 2;
        // From 45 % larger than that (only what stands out) down to 6 % (nearly anything larger).
        var cut = body * (1.45 - 0.39 * readiness);
        // What is printed on many pages in the same words is a page header, not a heading.
        var repeated = lines.GroupBy(entry => entry.Printed.Line.Text).Where(group => group.Select(entry => entry.Page).Distinct().Count() > 3)
            .Select(group => group.Key).ToHashSet();

        // A heading is set larger than the text. Or it is a short line of its own in bold at the size of the text,
        // which is how many documents set their smaller headings: counted unless asked to be choosy, and always a
        // close call.
        bool Larger(PrintedLine printed) => printed.Size >= cut;
        bool BoldOnly(PrintedLine printed) => !Larger(printed) && readiness >= 0.35 && printed.Bold && !bodyIsBold && printed.Size >= body * 0.98 &&
                                              printed.Line.Text.Length <= 80;
        var headings = lines.Where(entry => IsHeading(entry.Printed.Line.Text) && !repeated.Contains(entry.Printed.Line.Text) &&
                                            (Larger(entry.Printed) || BoldOnly(entry.Printed))).ToList();
        if (headings.Count == 0) return [];

        // The largest print is the outermost level, bold at the size of the text the innermost; up to three are told apart.
        var sizes = headings.Select(entry => Larger(entry.Printed) ? entry.Printed.Size : 0).Distinct().OrderDescending().ToList();
        var suggested = new List<SuggestedBookmark>();
        foreach (var (page, printed) in headings)
        {
            var line = printed.Line;
            var level = Math.Min(2, sizes.IndexOf(Larger(printed) ? printed.Size : 0));
            // A heading that runs over two lines is one heading.
            if (suggested.Count > 0 && suggested[^1].Bookmark is { } last && last.Page == page && last.Level == level &&
                last.Top is { } top && line.Bounds.Top - top < 2.8 * printed.Size && line.Bounds.Top > top)
            {
                suggested[^1] = suggested[^1] with { Bookmark = last with { Title = Shortened(last.Title + " " + line.Text) } };
                continue;
            }

            // A little above the line, so that the heading is not right at the edge of the window.
            var down = Math.Max(0, line.Bounds.Top - 0.6 * printed.Size);
            suggested.Add(new SuggestedBookmark(new Bookmark(Shortened(line.Text), page, level, down < 1 ? null : down),
                !Larger(printed) || printed.Size < cut * 1.06));
        }

        return suggested;
    }

    /// <summary>A heading says something in a few words: it has letters, and is neither a number alone nor a paragraph.</summary>
    private static bool IsHeading(string text) => text.Length is >= 2 and <= 140 && text.Count(char.IsLetter) >= 2;

    private static string Shortened(string title) => title.Length <= 120 ? title : title[..117].TrimEnd() + "…";

    /// <summary>
    /// What to call a bookmark for a page when nothing was typed: the largest line among its first few, which is
    /// what a heading looks like. Null for a page without text.
    /// </summary>
    public static string? TitleFor(PdfView view, int pageIndex)
    {
        var first = view.GetPrintedLines(pageIndex).Where(printed => IsHeading(printed.Line.Text)).Take(8).ToList();
        return first.Count == 0 ? null : Shortened(first.MaxBy(printed => printed.Size)!.Line.Text);
    }
}
