using System.Reflection;
using System.Runtime.InteropServices;
using PdfModifier.Core.Pdfium;

namespace PdfModifier.Tests;

public class NativeBindingTests
{
    [Fact]
    public void EveryImportedFunctionExistsInThePdfiumBinary()
    {
        // Load the library the way the app does (initialising PDFium twice while it is in use crashes it).
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.CreateNew();
        }

        Assert.True(NativeLibrary.TryLoad("pdfium", typeof(Native).Assembly, null, out var library));

        var missing = typeof(Native)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() != null)
            .Select(m => m.Name)
            .Where(name => !NativeLibrary.TryGetExport(library, name, out _))
            .ToList();

        Assert.True(missing.Count == 0, "Missing exports: " + string.Join(", ", missing));
    }
}
