using System.Reflection;

namespace PdfModifier;

public static class AppInfo
{
    public const string Name = "Master PDF Modifier";

    /// <summary>
    /// What the windows call themselves towards a Linux desktop. The menu entry (Services/DesktopEntry and
    /// packaging/linux) names the same under StartupWMClass; that is how the desktop knows a window of this program
    /// and gives it its icon.
    /// </summary>
    public const string WindowClass = "MasterPdfModifier";

    /// <summary>
    /// The name of the mark an open app sets (a named mutex). The setup for Windows knows it by the same name, as
    /// AppMark in packaging/windows/setup.iss.
    /// </summary>
    public const string OpenMark = "MasterPdfModifier.Open";

    /// <summary>Where the app is published: its source, and each version with its downloads.</summary>
    public const string Repository = "https://github.com/ZeronTwoi/MasterPdfModifier";

    /// <summary>The page of the newest version, with its downloads.</summary>
    public const string LatestVersionPage = Repository + "/releases/latest";

    /// <summary>Where the "Support the project" button leads. The button is hidden while this is empty.</summary>
    public const string DonationUrl = "";

    public static string Version =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
}
