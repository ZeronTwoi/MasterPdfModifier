using PdfModifier.Core.Text;

namespace PdfModifier.Core.Ocr;

/// <summary>Manages the Tesseract language files, which are downloaded on demand into the user's app data folder.</summary>
public static class TessData
{
    // The official Tesseract repository, reached two ways; the second is tried when the first is unreachable.
    private static readonly string[] Sources =
    [
        "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/",
        "https://github.com/tesseract-ocr/tessdata_fast/raw/main/"
    ];

    /// <summary>
    /// Folder holding the *.traineddata files. Defaults to the user's app data folder; the PDFMODIFIER_TESSDATA
    /// environment variable redirects it, e.g. for a portable install.
    /// </summary>
    public static string Directory { get; set; } =
        Environment.GetEnvironmentVariable("PDFMODIFIER_TESSDATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "PdfModifier", "tessdata");

    public static bool IsInstalled(string languageCode) => IsInstalledByTesseractCode(Languages.Get(languageCode).TesseractCode);

    internal static bool IsInstalledByTesseractCode(string tesseractCode) =>
        File.Exists(Path.Combine(Directory, tesseractCode + ".traineddata"));

    public static IReadOnlyList<string> Missing(IEnumerable<string> languageCodes) =>
        languageCodes.Where(code => !IsInstalled(code)).ToList();

    /// <summary>Downloads the recognition data for one language (2-15 MB) from the official Tesseract repository.</summary>
    public static async Task DownloadAsync(string languageCode, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var code = Languages.Get(languageCode).TesseractCode;
        System.IO.Directory.CreateDirectory(Directory);
        var target = Path.Combine(Directory, code + ".traineddata");
        var partial = target + ".part";

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(Sources[attempt] + code + ".traineddata", HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total is > 0) progress?.Report((double)done / total.Value);
                }

                break;
            }
            catch (Exception e) when (e is HttpRequestException or IOException && attempt + 1 < Sources.Length)
            {
                // Fall through to the next source.
            }
        }

        // Written under a temporary name so an interrupted download never looks like a complete file.
        File.Move(partial, target, true);
        progress?.Report(1);
    }
}
