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
        await Download.ToFileAsync(Sources.Select(source => source + code + ".traineddata").ToList(), target, progress, cancel);
    }
}
