namespace PdfModifier.Services;

/// <summary>How the app came onto this computer, which decides how a newer version takes its place.</summary>
public enum InstallKind
{
    /// <summary>None of the downloads: a build from the source, say, started from its output folder.</summary>
    Unknown,

    /// <summary>Windows: put there by the setup, which is also what removes it.</summary>
    Setup,

    /// <summary>The whole app in one file, lying wherever its user put it.</summary>
    OneFile,

    /// <summary>Linux: the package, installed for all users.</summary>
    Package
}

/// <summary>The copy of the app that is running: what kind of download it is and where its program file lies.</summary>
public sealed record Installation(InstallKind Kind, string Program)
{
    /// <summary>What the setup for Windows leaves next to the program, and what removes the app again.</summary>
    public const string Uninstaller = "unins000.exe";

    /// <summary>Where the Linux package puts the program (see packaging/linux/pack.sh).</summary>
    public const string PackageProgram = "/opt/masterpdfmodifier/MasterPdfModifier";

    public static Installation Current { get; } = Find(Environment.ProcessPath, AppContext.BaseDirectory, OperatingSystem.IsWindows());

    internal static Installation Find(string? program, string baseDirectory, bool windows)
    {
        if (string.IsNullOrEmpty(program) || Path.GetDirectoryName(Path.GetFullPath(program)) is not { } folder)
            return new Installation(InstallKind.Unknown, program ?? "");
        if (!windows && program == PackageProgram) return new Installation(InstallKind.Package, program);
        if (windows && File.Exists(Path.Combine(folder, Uninstaller))) return new Installation(InstallKind.Setup, program);
        // The one-file app unpacks what it is made of into another folder and runs from there; anything else has
        // its parts lying next to the program.
        var unpacked = !SameFolder(folder, baseDirectory);
        return new Installation(unpacked ? InstallKind.OneFile : InstallKind.Unknown, program);
    }

    internal static bool SameFolder(string one, string other) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(one)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
