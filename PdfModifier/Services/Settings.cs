using System.Text.Json;
using PdfModifier.Core.Text;

namespace PdfModifier.Services;

/// <summary>User preferences, stored as JSON in the per-user configuration folder.</summary>
public sealed class Settings
{
    private const int MaximumRecent = 12;

    public string UiLanguage { get; set; } = Languages.Default;

    /// <summary>Languages to look for when detecting text in scans.</summary>
    public List<string> OcrLanguages { get; set; } = [Languages.Default];

    /// <summary>With several languages selected, work out per page which of them it is written in.</summary>
    public bool AutoDetectLanguage { get; set; } = true;

    /// <summary>Image files placed on pages or used as watermarks, newest first, so they can be used again with one click.</summary>
    public List<string> RecentImages { get; set; } = [];

    /// <summary>Image files that are kept on offer for good: they stand before the recent ones and are never pushed out by them.</summary>
    public List<string> PinnedImages { get; set; } = [];

    /// <summary>Read scans with every means there is: slower, for pages that are blurred, coarse or pale.</summary>
    public bool OcrCareful { get; set; }

    /// <summary>
    /// Linux: the question whether to put the app into the application menu has been asked. It is asked once; the
    /// settings have a button for a later yes.
    /// </summary>
    public bool MenuEntryOffered { get; set; }

    /// <summary>Where the preferences are stored. Tests point this at a scratch folder.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "PdfModifier", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) is { } loaded)
            {
                loaded.UiLanguage = Languages.Get(loaded.UiLanguage).Code;
                loaded.OcrLanguages = (loaded.OcrLanguages ?? []).Where(Languages.IsSupported).Distinct().ToList();
                if (loaded.OcrLanguages.Count == 0) loaded.OcrLanguages.Add(Languages.Default);
                loaded.PinnedImages = (loaded.PinnedImages ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                loaded.RecentImages = (loaded.RecentImages ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && !loaded.IsPinned(p))
                    .Take(MaximumRecent).ToList();
                return loaded;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable preferences are not worth failing the start for; fall back to the defaults.
        }

        return new Settings();
    }

    public bool IsPinned(string path) => PinnedImages.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The images on offer under an image field: the pinned ones in the order they were pinned, then the recent ones, newest first.</summary>
    public IEnumerable<(string Path, bool Pinned)> ImageHistory() =>
        PinnedImages.Select(p => (p, true)).Concat(RecentImages.Select(p => (p, false)));

    /// <summary>Puts an image file at the top of the recently used ones. A pinned one stays where it is.</summary>
    public void RememberImage(string path)
    {
        if (IsPinned(path)) return;
        RecentImages.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        RecentImages.Insert(0, path);
        if (RecentImages.Count > MaximumRecent) RecentImages.RemoveRange(MaximumRecent, RecentImages.Count - MaximumRecent);
        Save();
    }

    /// <summary>Pins an image, or lets go of it again; let go of, it is the most recent of the others.</summary>
    public void PinImage(string path, bool pinned)
    {
        PinnedImages.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        RecentImages.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (pinned) PinnedImages.Add(path);
        else RecentImages.Insert(0, path);
        if (RecentImages.Count > MaximumRecent) RecentImages.RemoveRange(MaximumRecent, RecentImages.Count - MaximumRecent);
        Save();
    }

    /// <summary>Takes an image off the list, pinned or not. The file itself is not touched.</summary>
    public void ForgetImage(string path)
    {
        PinnedImages.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        RecentImages.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences that cannot be written simply do not persist.
        }
    }
}
