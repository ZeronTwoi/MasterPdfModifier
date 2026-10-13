using Avalonia;
using PdfModifier.Core;

namespace PdfModifier;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [SelfCheck.Argument, ..]) return SelfCheck.Run(Console.Out);

        // Before anything heavy starts: long jobs must not crawl just because another window is in front.
        Performance.RunAtFullSpeed();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = AppInfo.WindowClass })
            .LogToTrace();
}
