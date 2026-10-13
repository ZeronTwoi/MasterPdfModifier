using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PdfModifier.Localization;
using PdfModifier.Services;
using PdfModifier.ViewModels;
using PdfModifier.Views;

namespace PdfModifier;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Settings.Load();
            Loc.SetLanguage(settings.UiLanguage);
            var viewModel = new MainViewModel(settings);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => viewModel.Session.Dispose();

            // Files passed on the command line (e.g. "Open with") are opened once the window is up: a PDF as the
            // document, pictures as the pages of a new one.
            if (desktop.Args?.Where(File.Exists).ToList() is { Count: > 0 } files)
                window.Opened += async (_, _) => await viewModel.DropFiles(files);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
