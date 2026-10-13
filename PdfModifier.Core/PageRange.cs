namespace PdfModifier.Core;

/// <summary>Parses page selections such as "1-3, 7, 10-" into zero-based page indexes.</summary>
public static class PageRange
{
    /// <summary>
    /// Returns the pages in the order written. Numbers are one-based; "5-" runs to the last page and "-3" starts at the first.
    /// An empty string selects every page.
    /// </summary>
    public static List<int> Parse(string? text, int pageCount)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(text))
        {
            result.AddRange(Enumerable.Range(0, pageCount));
            return result;
        }

        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            int first, last;
            if (dash < 0)
            {
                first = last = ParseNumber(raw);
            }
            else
            {
                var left = raw[..dash].Trim();
                var right = raw[(dash + 1)..].Trim();
                first = left.Length == 0 ? 1 : ParseNumber(left);
                last = right.Length == 0 ? pageCount : ParseNumber(right);
            }

            if (first < 1 || last < 1) throw new FormatException($"'{raw}' is not a valid page range.");
            first = Math.Min(first, pageCount);
            last = Math.Min(last, pageCount);
            if (pageCount == 0) continue;
            var step = first <= last ? 1 : -1;
            for (var p = first; p != last + step; p += step) result.Add(p - 1);
        }

        return result;
    }

    /// <summary>Splits "1-3; 4-10" style input into one page list per output file. Groups are separated by ';' or new lines.</summary>
    public static List<List<int>> ParseGroups(string text, int pageCount) =>
        text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(group => Parse(group, pageCount))
            .Where(group => group.Count > 0)
            .ToList();

    /// <summary>Writes zero-based page indexes as a selection: "1-3, 7, 10-12". The reverse of <see cref="Parse"/> for sorted input.</summary>
    public static string Format(IEnumerable<int> pages)
    {
        var parts = new List<string>();
        int? first = null, last = null;

        void Close()
        {
            if (first is not { } a || last is not { } b) return;
            parts.Add(a == b ? (a + 1).ToString() : $"{a + 1}-{b + 1}");
        }

        foreach (var page in pages.Distinct().Order())
        {
            if (last is { } previous && page == previous + 1)
            {
                last = page;
                continue;
            }

            Close();
            first = last = page;
        }

        Close();
        return string.Join(", ", parts);
    }

    private static int ParseNumber(string s) =>
        int.TryParse(s, out var n) ? n : throw new FormatException($"'{s}' is not a page number.");
}
