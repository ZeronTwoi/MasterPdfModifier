using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfModifier.Core.Ocr;

/// <summary>What has to be installed before text recognition can run, as far as the app can see to that itself.</summary>
public enum OcrNeed
{
    Nothing,

    /// <summary>Windows: the built-in engine is there, the Microsoft Visual C++ runtime it is built on is not.</summary>
    Runtime,

    /// <summary>Linux: the Tesseract program is not installed.</summary>
    Program
}

public static class OcrEngine
{
    /// <summary>
    /// What stands between this computer and text recognition. <see cref="OcrNeed.Nothing"/> also when recognition
    /// cannot run for a reason the app has no remedy for.
    /// </summary>
    public static OcrNeed Need()
    {
        if (OcrBackends.Probe().Available) return OcrNeed.Nothing;
        if (OperatingSystem.IsWindows()) return VcRuntime.IsInstalled() ? OcrNeed.Nothing : OcrNeed.Runtime;
        return LinuxPackages.ForTesseract() != null ? OcrNeed.Program : OcrNeed.Nothing;
    }

    /// <summary>Has the next use look for an engine again, after something it needs was installed.</summary>
    public static void LookAgain() => OcrBackends.Reset();
}

/// <summary>
/// The Microsoft Visual C++ runtime, which the built-in text recognition is built on. Windows does not bring it;
/// most computers have it from some other program, a freshly set-up one has not.
/// </summary>
public static class VcRuntime
{
    /// <summary>Microsoft's own, lasting address of the current installer for 64-bit Windows.</summary>
    public const string Source = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    // What the libraries of the engine ask Windows for when they are loaded.
    private static readonly string[] Libraries = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];

    public static bool IsInstalled() => !OperatingSystem.IsWindows() || IsInstalled(CanLoad);

    /// <summary>The runtime counts as there only when all of it is.</summary>
    internal static bool IsInstalled(Func<string, bool> canLoad) => Libraries.All(canLoad);

    private static bool CanLoad(string name)
    {
        if (!NativeLibrary.TryLoad(name, out var library)) return false;
        NativeLibrary.Free(library);
        return true;
    }

    /// <summary>Fetches Microsoft's installer into the temp folder and says where it is.</summary>
    public static async Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "PdfModifier", "setup");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "vc_redist.x64.exe");
        await Download.ToFileAsync([Source], target, progress, cancel);
        return target;
    }

    /// <summary>
    /// True when Windows vouches for the signature the file carries and the signature is Microsoft's. A program
    /// fetched from the internet is not started before this is known.
    /// </summary>
    public static unsafe bool IsFromMicrosoft(string file)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        fixed (char* path = file)
        {
            var info = new TrustFile { Size = (uint)sizeof(TrustFile), Path = path };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData), Interface = NoInterface, UnionChoice = ChoiceFile, File = &info, Flags = NoRevocationCheck
            };
            if (WinVerifyTrust(-1, &action, &data) != 0) return false;
        }

        try
        {
            // The only managed way to ask a signed program file who signed it.
#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            return signer.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the installer, which shows its progress in a small window and has Windows ask for permission. True when
    /// the runtime is there afterwards.
    /// </summary>
    public static async Task<bool> InstallAsync(string installer, CancellationToken cancel = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installer, "/install /passive /norestart") { UseShellExecute = true });
            if (process != null) await process.WaitForExitAsync(cancel);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Permission was not given.
        }

        return IsInstalled();
    }

    private const uint NoInterface = 2, ChoiceFile = 1, NoRevocationCheck = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TrustFile
    {
        public uint Size;
        public char* Path;
        public nint Handle;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData, SipClientData;
        public uint Interface, RevocationChecks, UnionChoice;
        public TrustFile* File;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint Flags, InterfaceContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll")]
    private static extern unsafe int WinVerifyTrust(nint window, Guid* action, TrustData* data);
}

/// <summary>
/// Installing a program on Linux through the package manager of the distribution. It is started with pkexec, which
/// has the desktop ask for the password in a window of its own, so no terminal is involved.
/// </summary>
public static class LinuxPackages
{
    public sealed record Command(string Program, IReadOnlyList<string> Arguments);

    /// <summary>How Tesseract is installed here, or null where there is no package manager this knows or no pkexec.</summary>
    public static Command? ForTesseract() => OperatingSystem.IsLinux() ? ForTesseract(OnPath) : null;

    internal static Command? ForTesseract(Func<string, bool> has)
    {
        if (!has("pkexec")) return null;
        // Each distribution names the package its own way; English comes along with all of them.
        string[]? install =
            has("apt-get") ? ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "tesseract-ocr"]
            : has("dnf") ? ["dnf", "install", "-y", "tesseract"]
            : has("zypper") ? ["zypper", "--non-interactive", "install", "tesseract-ocr"]
            : has("pacman") ? ["pacman", "-S", "--noconfirm", "--needed", "tesseract", "tesseract-data-eng"]
            : null;
        return install == null ? null : new Command("pkexec", install);
    }

    /// <summary>Runs the command and waits for it. True when it reports success.</summary>
    public static async Task<bool> RunAsync(Command command, CancellationToken cancel = default)
    {
        var info = new ProcessStartInfo(command.Program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process == null) return false;
            await process.WaitForExitAsync(cancel);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool OnPath(string program) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator)
        .Any(folder => folder.Length > 0 && File.Exists(Path.Combine(folder, program)));
}
