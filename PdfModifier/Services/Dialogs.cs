using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PdfModifier.Localization;

namespace PdfModifier.Services;

/// <summary>File pickers and small modal prompts, owned by the main window.</summary>
public sealed class Dialogs(Window owner)
{
    private readonly SemaphoreSlim _turn = new(1, 1);

    public static FilePickerFileType PdfFiles => new(Loc.Get("FilterPdf")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] };

    public static FilePickerFileType ImageFiles => new(Loc.Get("FilterImages"))
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif"]
    };

    public static FilePickerFileType TextFiles => new(Loc.Get("FilterText")) { Patterns = ["*.txt"], MimeTypes = ["text/plain"] };

    /// <summary>Whatever can be added to a document as pages.</summary>
    public static FilePickerFileType PageFiles => new(Loc.Get("FilterPages"))
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif", "*.pdf"]
    };

    public async Task<IReadOnlyList<string>> OpenFiles(string title, bool multiple, params FilePickerFileType[] types)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = multiple, FileTypeFilter = types
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> SaveFile(string title, string suggestedName, FilePickerFileType type)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            FileTypeChoices = [type]
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolder(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task Message(string text) => Show(text, null, false);

    public async Task<bool> Confirm(string text) => await Show(text, null, true) != null;

    /// <summary>A question whose two answers are named for what they do, rather than OK and Cancel.</summary>
    public async Task<bool> Confirm(string text, string yes, string no) => await Show(text, null, true, yes, no) != null;

    /// <summary>Asks for a password; null when cancelled.</summary>
    public Task<string?> AskPassword(string prompt)
    {
        var box = new TextBox { PasswordChar = '•', MinWidth = 280 };
        return Show(prompt, box, true);
    }

    private async Task<string?> Show(string text, TextBox? input, bool cancellable, string? yes = null, string? no = null)
    {
        var window = new Window
        {
            Title = AppInfo.Name,
            Icon = owner.Icon,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        string? result = null;
        var ok = new Button { Content = yes ?? Loc.Get("Ok"), IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            result = input?.Text ?? "";
            window.Close();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        if (cancellable)
        {
            var cancel = new Button { Content = no ?? Loc.Get("Cancel"), IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => window.Close();
            buttons.Children.Add(cancel);
        }

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (input != null) panel.Children.Add(input);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Opened += (_, _) => (input as Control ?? ok).Focus();

        // One question at a time: two that come up together (at the first start, say) wait their turn.
        await _turn.WaitAsync();
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            _turn.Release();
        }

        return result;
    }
}
