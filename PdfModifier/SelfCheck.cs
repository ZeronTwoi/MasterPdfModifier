using System.Runtime.InteropServices;
using PdfModifier.Core.Operations;
using SkiaSharp;

namespace PdfModifier;

/// <summary>
/// What the app does when it is started with "--check": it tries the parts it is built on, without opening a window,
/// and says what it found. That tells whether a build that was just made, or a download that will not work, has
/// everything it needs. The answer is also the exit code: 0 when the app can do its work.
/// </summary>
internal static class SelfCheck
{
    public const string Argument = "--check";

    public static int Run(TextWriter output)
    {
        var failed = false;

        void Try(string part, Func<string> probe)
        {
            try
            {
                output.WriteLine($"ok    {part}: {probe()}");
            }
            catch (Exception e)
            {
                failed = true;
                output.WriteLine($"FAIL  {part}: {e.GetType().Name}: {e.Message}");
            }
        }

        output.WriteLine($"{AppInfo.Name} {AppInfo.Version}, {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");

        byte[] picture = [];
        Try("Imaging", () =>
        {
            using var bitmap = new SKBitmap(120, 160);
            bitmap.Erase(SKColors.White);
            picture = PdfImages.Encode(bitmap, ImageFormat.Png);
            using var read = PdfImages.Decode(picture);
            return $"a picture of {read.Width} by {read.Height} written and read";
        });
        Try("PDF engine", () =>
        {
            var pdf = PdfImages.InsertImages(null, [picture], 0);
            using var view = new PdfView(pdf);
            using var page = view.Render(0, 1);
            return $"a document of {view.PageCount} page made and shown at {page.Width} by {page.Height}";
        });

        // Not a failure: the app offers to fetch what text recognition lacks when it is first asked for.
        var (available, detail) = PdfOcr.EngineStatus();
        output.WriteLine(available ? $"ok    Text recognition: {detail}" : $"note  Text recognition: {detail}");
        output.WriteLine(failed ? "Something the app needs is not working." : "Everything the app needs is working.");
        return failed ? 1 : 0;
    }
}
