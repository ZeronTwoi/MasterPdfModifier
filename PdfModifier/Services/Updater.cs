using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using PdfModifier.Core.Ocr;

namespace PdfModifier.Services;

/// <summary>A newer version, fetched and checked, that waits to take the place of the running one.</summary>
public sealed record ReadyUpdate(string Version, string File);

/// <summary>
/// Puts a newer version in the place of the running one, when the user presses the button for it. It fetches the
/// download that fits how the app is on this computer, checks it against the checksums published with the version
/// and hands over to it:
///
/// - installed by the Windows setup: the new setup is run without questions; it waits for the app to close, installs
///   over it with the choices made the first time and starts it again;
/// - the one-file app: the new file takes the place of the running one, which steps aside under another name and is
///   deleted by the new one when it starts;
/// - the Linux package: the new package is installed through the package manager, which asks for the password.
///
/// Nothing here runs unless that button was pressed, and nothing is fetched from anywhere but GitHub.
/// </summary>
public sealed class Updater
{
    /// <summary>The file each version is published with: the SHA-256 of every download, one per line.</summary>
    public const string Checksums = "SHA256SUMS.txt";

    /// <summary>What a running program is called while its successor takes its place (Windows).</summary>
    public const string SteppedAside = ".old";

    private const string TagPath = "/releases/tag/", DownloadPath = "/releases/download/";
    private const string ProgramInArchive = "MasterPdfModifier";

    /// <summary>
    /// Where downloads are kept until they are used: in the user's own data folder, where nobody else can put
    /// something in their place. Cleared when the app next starts (see <see cref="Leftovers"/>).
    /// </summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "PdfModifier", "update");

    // What is looked at and what is done. The tests put their own in place of the real thing.
    internal Installation Installed { get; set; } = Installation.Current;
    internal bool Windows { get; set; } = OperatingSystem.IsWindows();
    internal string Folder { get; set; } = DefaultFolder;
    internal Func<string, string, IProgress<double>?, CancellationToken, Task> Fetch { get; set; } = FromGitHub;
    internal Func<ProcessStartInfo, bool> Start { get; set; } = StartProcess;
    internal Func<string, CancellationToken, Task<bool>> InstallPackage { get; set; } = InstallWithPackageManager;
    internal Func<string, int> Running { get; set; } = CountRunning;

    /// <summary>The download that fits this copy of the app; null for a copy that is none of the downloads.</summary>
    public string? Asset => AssetFor(Installed.Kind, Windows);

    /// <summary>True when the app can put a newer version in its own place here.</summary>
    public bool CanUpdate => Asset != null && (Installed.Kind != InstallKind.Package || LinuxPackages.ForPackageFile(Installed.Program) != null);

    /// <summary>
    /// True while the same program is open in another window. On Windows that one has to be closed first: its
    /// program file is about to be replaced.
    /// </summary>
    public bool OthersOpen => Windows && Running(Installed.Program) > 1;

    internal static string? AssetFor(InstallKind kind, bool windows) => (kind, windows) switch
    {
        (InstallKind.Setup, true) => "MasterPdfModifier-Setup.exe",
        (InstallKind.OneFile, true) => "MasterPdfModifier.exe",
        (InstallKind.OneFile, false) => "MasterPdfModifier-Linux.tar.gz",
        (InstallKind.Package, false) => "MasterPdfModifier.deb",
        _ => null
    };

    /// <summary>Where a file of a version is downloaded from, given the address of that version's page.</summary>
    internal static string DownloadAddress(string page, string file)
    {
        var cut = page.LastIndexOf(TagPath, StringComparison.Ordinal);
        if (cut < 0) throw new ArgumentException("Not the page of a version: " + page, nameof(page));
        return page[..cut] + DownloadPath + page[(cut + TagPath.Length)..] + "/" + file;
    }

    /// <summary>
    /// Fetches the download of a version and checks it. A file that is not what was published is deleted and
    /// reported as <see cref="InvalidDataException"/>; it is never started.
    /// </summary>
    /// <param name="page">The page of the version, as <see cref="UpdateCheck.Page"/> has it.</param>
    public async Task<ReadyUpdate> Prepare(string page, string version, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var asset = Asset ?? throw new InvalidOperationException("This copy of the app cannot update itself.");
        var folder = Path.Combine(Folder, version);
        Directory.CreateDirectory(folder);
        var sums = Path.Combine(folder, Checksums);
        await Fetch(DownloadAddress(page, Checksums), sums, null, cancel);
        var expected = ChecksumIn(await File.ReadAllTextAsync(sums, cancel), asset) ?? throw new InvalidDataException("No checksum is published for " + asset);

        // Pressed a second time, after closing another window or saving: what is there and right is not fetched again.
        var file = Path.Combine(folder, asset);
        if (!File.Exists(file) || await ChecksumOf(file, cancel) != expected) await Fetch(DownloadAddress(page, asset), file, progress, cancel);
        if (await ChecksumOf(file, cancel) != expected)
        {
            File.Delete(file);
            throw new InvalidDataException("The download does not match its published checksum.");
        }

        progress?.Report(1);
        return new ReadyUpdate(version, file);
    }

    /// <summary>
    /// Hands over to the new version and has the app start again, with the document that was open. The app has to
    /// close once this returns.
    /// </summary>
    /// <param name="reopen">The file to open again after the update; null for none.</param>
    public async Task Apply(ReadyUpdate ready, string? reopen, CancellationToken cancel = default)
    {
        switch (Installed.Kind)
        {
            case InstallKind.Setup when Windows:
                // Without questions and without a way to stop half-way; see [Code] in packaging/windows/setup.iss
                // for what the last two do.
                Launch(new ProcessStartInfo(ready.File, "/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /UPDATE=1" + (reopen == null ? "" : $" /REOPEN=\"{reopen}\""))
                {
                    UseShellExecute = false
                });
                break;
            case InstallKind.OneFile:
                var program = Installed.Program;
                // Looked at first: a folder that cannot be written to must not cost the program that is there.
                var folder = Path.GetDirectoryName(program)!;
                using (File.Create(Path.Combine(folder, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose))
                {
                }

                if (Windows) StepAside(program, ready.File);
                else File.Move(Unpack(ready.File, program + ".new"), program, true);
                Launch(Again(program, reopen));
                break;
            case InstallKind.Package when !Windows:
                if (!await InstallPackage(ready.File, cancel)) throw new IOException("The package was not installed.");
                Launch(Again(Installed.Program, reopen));
                break;
            default:
                throw new InvalidOperationException("This copy of the app cannot update itself.");
        }
    }

    /// <summary>
    /// Windows lets a running program be renamed, though not overwritten or deleted: the old one steps aside, the
    /// new one moves in under its name.
    /// </summary>
    private static void StepAside(string program, string replacement)
    {
        var old = program + SteppedAside;
        File.Delete(old);
        File.Move(program, old);
        try
        {
            File.Move(replacement, program);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Move(old, program);
            throw;
        }
    }

    /// <summary>Takes the program out of the Linux archive, as a file that can be started.</summary>
    internal static string Unpack(string archive, string target)
    {
        using var packed = File.OpenRead(archive);
        using var plain = new GZipStream(packed, CompressionMode.Decompress);
        using var files = new TarReader(plain);
        while (files.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Name != ProgramInArchive) continue;
            entry.ExtractToFile(target, true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                             UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return target;
        }

        throw new InvalidDataException("The archive does not hold the program.");
    }

    private static ProcessStartInfo Again(string program, string? reopen)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false };
        if (reopen != null) info.ArgumentList.Add(reopen);
        return info;
    }

    private void Launch(ProcessStartInfo info)
    {
        if (!Start(info)) throw new IOException("Could not start " + info.FileName);
    }

    /// <summary>The checksum a list of the form "hash  file name" has for a file, in lower case; null when it has none.</summary>
    internal static string? ChecksumIn(string list, string file)
    {
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ');
            if (space != 64 || line[space..].TrimStart(' ', '*') != file) continue;
            var hash = line[..space].ToLowerInvariant();
            if (hash.All(Uri.IsHexDigit)) return hash;
        }

        return null;
    }

    internal static async Task<string> ChecksumOf(string file, CancellationToken cancel = default)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
    }

    /// <summary>True for a secure address at GitHub or at the servers it keeps its downloads on.</summary>
    internal static bool IsGitHub(Uri address) => address.Scheme == Uri.UriSchemeHttps &&
                                                  (address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                                                   address.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes what an address holds into a file. GitHub passes a download on to the server that has it; every step
    /// of the way has to be one of its own addresses, or the download is given up.
    /// </summary>
    internal static async Task FromGitHub(string address, string target, IProgress<double>? progress, CancellationToken cancel)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var from = new Uri(address);
        for (var hop = 0; ; hop++)
        {
            if (hop > 5 || !IsGitHub(from)) throw new HttpRequestException("The download led away from GitHub: " + from.Host);
            using var request = new HttpRequestMessage(HttpMethod.Get, from);
            request.Headers.UserAgent.ParseAdd("MasterPdfModifier/" + AppInfo.Version);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                from = location.IsAbsoluteUri ? location : new Uri(from, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            // Written under a temporary name so an interrupted download never looks like a complete file.
            var partial = target + ".part";
            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total is > 0) progress?.Report((double)done / total.Value);
                }
            }

            File.Move(partial, target, true);
            return;
        }
    }

    private static bool StartProcess(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        return process != null;
    }

    private static async Task<bool> InstallWithPackageManager(string file, CancellationToken cancel) =>
        LinuxPackages.ForPackageFile(file) is { } command && await LinuxPackages.RunAsync(command, cancel);

    /// <summary>How many copies of a program are running, this one included.</summary>
    internal static int CountRunning(string program)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program));
        foreach (var process in processes) process.Dispose();
        return processes.Length;
    }
}
