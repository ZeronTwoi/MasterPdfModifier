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

            // With a person in front of the window, what text recognition lacks is fetched after asking them.
            viewModel.Supplies.Ask = question => viewModel.Dialogs.Confirm(question, Loc.Get("SetupYes"), Loc.Get("SetupNo"));

            // Files passed on the command line (e.g. "Open with") are opened once the window is up: a PDF as the
            // document, pictures as the pages of a new one.
            if (desktop.Args?.Where(File.Exists).ToList() is { Count: > 0 } files)
                window.Opened += async (_, _) => await viewModel.DropFiles(files);
            else if (DesktopEntry.IsSupported)
                window.Opened += async (_, _) => await OfferMenuEntry(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Linux: a program that came as a single file is in no menu. The first time it is started by itself it asks
    /// whether it should put itself there; an entry it wrote earlier is kept pointing at where the program is now.
    /// </summary>
    private static async Task OfferMenuEntry(MainViewModel viewModel)
    {
        if (DesktopEntry.Program is not { } program) return;
        try
        {
            DesktopEntry.KeepUp(program);
            var settings = viewModel.Settings;
            if (settings.MenuEntryOffered || DesktopEntry.Exists()) return;
            settings.MenuEntryOffered = true;
            settings.Save();
            if (!await viewModel.Dialogs.Confirm(Loc.Format("MenuAsk", AppInfo.Name), Loc.Get("SetupYes"), Loc.Get("SetupNo"))) return;
            DesktopEntry.Add(program);
            viewModel.StatusText = Loc.Format("MenuAdded", AppInfo.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            viewModel.StatusText = MainViewModel.Describe(e);
        }
    }
}
