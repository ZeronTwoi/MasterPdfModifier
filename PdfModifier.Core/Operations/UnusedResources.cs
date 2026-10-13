using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace PdfModifier.Core.Operations;

/// <summary>Removes XObject resource entries that no page content draws, so the streams behind them can be collected.</summary>
internal static class UnusedResources
{
    public static void Prune(PdfDocument doc)
    {
        // Resource dictionaries are often shared between pages, so usage is gathered per dictionary first.
        var usage = new Dictionary<PdfDictionary, HashSet<string>?>(ReferenceEqualityComparer.Instance);
        foreach (PdfPage page in doc.Pages)
        {
            var xobjects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
            if (xobjects == null) continue;
            var used = UsedXObjects(page, xobjects);
            if (!usage.TryGetValue(xobjects, out var known)) usage[xobjects] = used;
            else if (known == null || used == null) usage[xobjects] = null;
            else known.UnionWith(used);
        }

        foreach (var (xobjects, used) in usage)
        {
            if (used == null) continue; // could not be analysed: leave untouched
            foreach (var key in xobjects.Elements.Keys.Where(k => !used.Contains(k)).ToList())
                xobjects.Elements.Remove(key);
        }
    }

    /// <summary>Names drawn with the Do operator, or null when the page cannot be analysed safely.</summary>
    private static HashSet<string>? UsedXObjects(PdfPage page, PdfDictionary xobjects)
    {
        try
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            Collect(ContentReader.ReadContent(page), used);
            foreach (var name in used)
            {
                // A form without its own resources borrows the page's, so any entry might still be in use.
                var target = xobjects.Elements.GetDictionary(name);
                if (target != null && target.Elements.GetName("/Subtype") == "/Form" && !target.Elements.ContainsKey("/Resources"))
                    return null;
            }

            return used;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void Collect(CSequence sequence, HashSet<string> used)
    {
        foreach (var item in sequence)
        {
            if (item is CSequence nested)
            {
                Collect(nested, used);
            }
            else if (item is COperator { OpCode.OpCodeName: OpCodeName.Do } op)
            {
                foreach (var operand in op.Operands)
                    if (operand is CName name)
                        used.Add(name.Name);
            }
        }
    }
}
