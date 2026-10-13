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

    /// <summary>Where the "Support the project" button leads. The button is hidden while this is empty.</summary>
    public const string DonationUrl = "";

    public static string Version =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
}
