using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfModifier.Core.Text;
using SkiaSharp;

namespace PdfModifier.Core.Ocr;

/// <summary>Text recognition is not available, or a language model is missing.</summary>
public class OcrException(string message) : Exception(message);

/// <summary>Raised when language data has to be downloaded first; <see cref="Languages"/> are app language codes.</summary>
public sealed class OcrLanguageMissingException(IReadOnlyList<string> languages)
    : OcrException("Language data is missing: " + string.Join(", ", languages))
{
    public IReadOnlyList<string> Languages { get; } = languages;
}

/// <summary>A recognised word with its box in image pixels.</summary>
internal sealed record RawWord(string Text, int Left, int Top, int Right, int Bottom, double Confidence);

internal sealed record RawLine(IReadOnlyList<RawWord> Words);

internal interface IOcrBackend
{
    string Name { get; }

    /// <summary>
    /// True when the engine has all of these languages (Tesseract codes) from somewhere else than the app's own data
    /// folder, so nothing needs to be downloaded for them.
    /// </summary>
    bool BringsLanguages(IReadOnlyList<string> tesseractCodes);

    /// <param name="languages">Tesseract codes joined with '+', e.g. "eng+rus".</param>
    /// <param name="sparse">
    /// Looks for text wherever it may be instead of assuming blocks of running text. Finds labels scattered over forms,
    /// tables and drawings that the block analysis leaves out, at the price of losing the reading order.
    /// </param>
    /// <param name="singleLine">
    /// The picture is one line of text and nothing else. A word or a number standing alone, which the page analysis
    /// does not take for text at all, is read this way.
    /// </param>
    List<RawLine> Recognize(SKBitmap image, string languages, int dpi, bool sparse = false, bool singleLine = false);
}

internal static class OcrBackends
{
    private static readonly object Gate = new();
    private static IOcrBackend? _backend;
    private static string? _failure;

    /// <summary>The first working engine: the bundled library on Windows, otherwise a tesseract executable on the PATH.</summary>
    public static IOcrBackend Get()
    {
        lock (Gate)
        {
            if (_backend != null) return _backend;
            if (_failure != null) throw new OcrException(_failure);

            if (OperatingSystem.IsWindows() && InProcessTesseract.TryCreate() is { } library) return _backend = library;
            if (TesseractCli.TryCreate() is { } cli) return _backend = cli;

            _failure = OperatingSystem.IsWindows()
                ? "The text recognition engine could not be loaded. It needs the Microsoft Visual C++ runtime."
                : "Text recognition needs the Tesseract program, which is not installed.";
            throw new OcrException(_failure);
        }
    }

    /// <summary>Forgets what was found, so the next use looks again: after something the engine needs was installed.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _backend = null;
            _failure = null;
        }
    }

    /// <summary>The languages (app codes) whose data has to be fetched before the engine can read them.</summary>
    public static IReadOnlyList<string> Missing(IOcrBackend backend, IEnumerable<string> languageCodes)
    {
        var codes = languageCodes.ToList();
        return backend.BringsLanguages(codes.Select(code => Languages.Get(code).TesseractCode).ToList()) ? [] : TessData.Missing(codes);
    }

    public static (bool Available, string Detail) Probe()
    {
        try
        {
            return (true, Get().Name);
        }
        catch (OcrException e)
        {
            return (false, e.Message);
        }
    }
}

/// <summary>Tesseract loaded into the process through the Tesseract NuGet package (ships Windows binaries).</summary>
internal sealed class InProcessTesseract : IOcrBackend
{
    private readonly object _gate = new();
    private string? _loadedLanguages;
    private Tesseract.TesseractEngine? _engine;

    public string Name => "Tesseract (built in)";
    public bool BringsLanguages(IReadOnlyList<string> tesseractCodes) => false;

    public static InProcessTesseract? TryCreate()
    {
        try
        {
            // Touching the native API fails here, not later, when the binaries cannot be loaded.
            using var probe = Tesseract.Pix.Create(1, 1, 8);
            return new InProcessTesseract();
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or BadImageFormatException
                                      or Tesseract.TesseractException or EntryPointNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }

    public List<RawLine> Recognize(SKBitmap image, string languages, int dpi, bool sparse = false, bool singleLine = false)
    {
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        lock (_gate)
        {
            if (_engine == null || _loadedLanguages != languages)
            {
                _engine?.Dispose();
                _engine = new Tesseract.TesseractEngine(TessData.Directory, languages, Tesseract.EngineMode.Default);
                _loadedLanguages = languages;
            }

            using var pix = Tesseract.Pix.LoadFromMemory(encoded.ToArray());
            pix.XRes = dpi;
            pix.YRes = dpi;
            using var page = _engine.Process(pix, singleLine ? Tesseract.PageSegMode.SingleLine
                : sparse ? Tesseract.PageSegMode.SparseText
                : Tesseract.PageSegMode.Auto);
            using var iterator = page.GetIterator();
            var lines = new List<RawLine>();
            iterator.Begin();
            do
            {
                do
                {
                    do
                    {
                        var words = new List<RawWord>();
                        do
                        {
                            var text = iterator.GetText(Tesseract.PageIteratorLevel.Word)?.Trim();
                            if (string.IsNullOrEmpty(text)) continue;
                            if (!iterator.TryGetBoundingBox(Tesseract.PageIteratorLevel.Word, out var box)) continue;
                            words.Add(new RawWord(text, box.X1, box.Y1, box.X2, box.Y2, iterator.GetConfidence(Tesseract.PageIteratorLevel.Word)));
                        } while (iterator.Next(Tesseract.PageIteratorLevel.TextLine, Tesseract.PageIteratorLevel.Word));

                        if (words.Count > 0) lines.Add(new RawLine(words));
                    } while (iterator.Next(Tesseract.PageIteratorLevel.Para, Tesseract.PageIteratorLevel.TextLine));
                } while (iterator.Next(Tesseract.PageIteratorLevel.Block, Tesseract.PageIteratorLevel.Para));
            } while (iterator.Next(Tesseract.PageIteratorLevel.Block));

            return lines;
        }
    }
}

/// <summary>Drives a tesseract executable found on the PATH and reads its TSV output.</summary>
internal sealed class TesseractCli(string executable) : IOcrBackend
{
    private HashSet<string>? _installed;

    public string Name => "Tesseract (" + executable + ")";

    /// <summary>The languages the program was installed with; the app's own downloads make up for the rest.</summary>
    public bool BringsLanguages(IReadOnlyList<string> tesseractCodes)
    {
        lock (this)
        {
            if (_installed == null)
            {
                try
                {
                    // Older versions print the list as a remark rather than as output.
                    var (_, output, remarks) = Run(executable, ["--list-langs"]);
                    _installed = ParseLanguageList(output + "\n" + remarks);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    _installed = [];
                }
            }

            return tesseractCodes.All(_installed.Contains);
        }
    }

    /// <summary>The codes in the answer to "tesseract --list-langs": a heading, then one language to a line.</summary>
    internal static HashSet<string> ParseLanguageList(string listing) => listing.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && line.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '/' or '-'))
        .ToHashSet();

    public static TesseractCli? TryCreate()
    {
        try
        {
            var (exit, _, _) = Run("tesseract", ["--version"]);
            return exit == 0 ? new TesseractCli("tesseract") : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public List<RawLine> Recognize(SKBitmap image, string languages, int dpi, bool sparse = false, bool singleLine = false)
    {
        var input = Path.Combine(Path.GetTempPath(), "pdfmodifier-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(input))
                encoded.SaveTo(file);

            var args = new List<string> { input, "stdout", "-l", languages, "--dpi", dpi.ToString(CultureInfo.InvariantCulture) };
            if (sparse || singleLine)
            {
                args.Add("--psm");
                args.Add(singleLine ? "7" : "11");
            }

            // Prefer the system's language data; fall back to the files this app downloaded.
            if (languages.Split('+').All(TessData.IsInstalledByTesseractCode))
            {
                args.Add("--tessdata-dir");
                args.Add(TessData.Directory);
            }

            args.Add("tsv");
            var (exit, output, error) = Run(executable, args);
            if (exit != 0) throw new OcrException("Tesseract failed: " + error.Trim());
            return ParseTsv(output);
        }
        finally
        {
            try
            {
                File.Delete(input);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }

    internal static List<RawLine> ParseTsv(string tsv)
    {
        var lines = new List<RawLine>();
        var current = new List<RawWord>();
        var currentKey = "";
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var f = row.TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5") continue;
            var text = f[11].Trim();
            if (text.Length == 0) continue;
            var key = $"{f[1]}.{f[2]}.{f[3]}.{f[4]}";
            if (key != currentKey && current.Count > 0)
            {
                lines.Add(new RawLine(current));
                current = [];
            }

            currentKey = key;
            int left = int.Parse(f[6], CultureInfo.InvariantCulture), top = int.Parse(f[7], CultureInfo.InvariantCulture);
            int width = int.Parse(f[8], CultureInfo.InvariantCulture), height = int.Parse(f[9], CultureInfo.InvariantCulture);
            var confidence = double.Parse(f[10], CultureInfo.InvariantCulture);
            current.Add(new RawWord(text, left, top, left + width, top + height, confidence));
        }

        if (current.Count > 0) lines.Add(new RawLine(current));
        return lines;
    }

    private static (int Exit, string Output, string Error) Run(string file, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }
}
