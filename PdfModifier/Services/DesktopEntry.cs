using System.Diagnostics;
using System.Text;
using Avalonia.Platform;

namespace PdfModifier.Services;

/// <summary>
/// The entry that puts the app into the application menu of a Linux desktop, with its icon, and offers it for
/// opening PDF files. A program file has neither a name nor an icon of its own there; the desktop takes both from
/// such an entry. It is written for the current user only, so nothing here needs special rights or a terminal.
/// </summary>
public static class DesktopEntry
{
    private const string Id = "masterpdfmodifier";

    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>The user's data folder as the desktop standard has it. Tests point this at a scratch folder.</summary>
    public static string DataFolder { get; set; } = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static string EntryFile => Path.Combine(DataFolder, "applications", Id + ".desktop");
    private static string PictureFile => Path.Combine(DataFolder, "icons", "hicolor", "256x256", "apps", Id + ".png");
    private static string DrawingFile => Path.Combine(DataFolder, "icons", "hicolor", "scalable", "apps", Id + ".svg");

    /// <summary>
    /// The program file an entry has to start: the one that is running. Inside an AppImage that is the image, which
    /// names itself in a variable, not the copy of the program it unpacks for a while.
    /// </summary>
    public static string? Program => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : Environment.ProcessPath;

    /// <summary>True when this entry is there: the one that can be taken out again here.</summary>
    public static bool IsOwn => File.Exists(EntryFile);

    /// <summary>True when the app is in the menu already, by this entry or by a package installed for all users.</summary>
    public static bool Exists() => IsOwn || File.Exists($"/usr/share/applications/{Id}.desktop");

    /// <summary>Writes the entry and its icon. The entry names the program by its full path, so it works from wherever that is.</summary>
    public static void Add(string program)
    {
        Copy("app.png", PictureFile);
        Copy("app.svg", DrawingFile);
        Directory.CreateDirectory(Path.GetDirectoryName(EntryFile)!);
        File.WriteAllText(EntryFile, Text(program), new UTF8Encoding(false));
        Announce();
    }

    /// <summary>
    /// Points an entry that is there at the program that is running now. A program that is a single file gets moved,
    /// or downloaded anew into another folder; the menu should go on starting it.
    /// </summary>
    public static void KeepUp(string program)
    {
        if (!IsOwn) return;
        var text = Text(program);
        if (File.ReadAllText(EntryFile) != text) File.WriteAllText(EntryFile, text, new UTF8Encoding(false));
    }

    public static void Remove()
    {
        foreach (var file in new[] { EntryFile, PictureFile, DrawingFile }) File.Delete(file);
        Announce();
    }

    internal static string Text(string program) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Version=1.0",
        "Name=" + AppInfo.Name,
        "GenericName=PDF Editor",
        "Comment=Edit, organise, clean up and compress PDF files",
        "Exec=" + Quote(program) + " %F",
        // Hides the entry by itself when the program file is no longer where it was.
        "TryExec=" + program.Replace("\\", "\\\\"),
        "Icon=" + Id,
        "Terminal=false",
        "Categories=Office;",
        "MimeType=application/pdf;",
        "Keywords=pdf;scan;ocr;merge;split;compress;watermark;",
        "StartupWMClass=" + AppInfo.WindowClass,
        "");

    /// <summary>
    /// A path the way the command line of an entry wants it: in quotes, with the characters that mean something
    /// inside them escaped, and that once more for the backslash, which an entry reads as an escape of its own.
    /// </summary>
    internal static string Quote(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
            quoted.Append(c switch
            {
                '\\' => "\\\\\\\\",
                '"' or '`' or '$' => "\\\\" + c,
                '%' => "%%",
                _ => c.ToString()
            });
        return quoted.Append('"').ToString();
    }

    private static void Copy(string asset, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var source = AssetLoader.Open(new Uri($"avares://{typeof(DesktopEntry).Assembly.GetName().Name}/Assets/{asset}"));
        using var file = File.Create(target);
        source.CopyTo(file);
    }

    /// <summary>Tells the desktop that its lists have changed, where it has the tools for that; it finds out by itself otherwise.</summary>
    private static void Announce()
    {
        if (!IsSupported) return;
        Run("update-desktop-database", Path.Combine(DataFolder, "applications"));
        Run("gtk-update-icon-cache", "-q", "-t", Path.Combine(DataFolder, "icons", "hicolor"));
    }

    private static void Run(string program, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            process?.WaitForExit(5000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The tool is not installed here.
        }
    }
}
