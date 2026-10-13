using Avalonia;
using PdfModifier.Core;
using PdfModifier.Services;

namespace PdfModifier;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [SelfCheck.Argument, ..]) return SelfCheck.Run(Console.Out);

        // The mark of an open app. The setup for Windows looks for it (packaging/windows/setup.iss): it waits for
        // the app to close before it puts a newer version in its place, and it does not remove an app that is open.
        using var open = Mark(out var first);
        // Versions from before there was a mark are known by their name alone; a window of one of those counts too.
        var alone = first && Updater.CountRunning(Installation.Current.Program) <= 1;
        Leftovers.ClearInBackground(Installation.Current, alone);

        // Before anything heavy starts: long jobs must not crawl just because another window is in front.
        Performance.RunAtFullSpeed();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = AppInfo.WindowClass })
            .LogToTrace();

    /// <summary>Sets the mark and says whether this is the only copy of the app that is open.</summary>
    private static Mutex? Mark(out bool alone)
    {
        try
        {
            return new Mutex(false, AppInfo.OpenMark, out alone);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            // Without the mark nothing is known about other copies, so nothing that could be theirs is touched.
            alone = false;
            return null;
        }
    }
}
