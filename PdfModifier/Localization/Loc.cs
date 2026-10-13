using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfModifier.Core.Text;

namespace PdfModifier.Localization;

/// <summary>
/// Interface texts in the eight supported languages. XAML reads them as dynamic resources named "T_" + key, so a
/// language switch updates every open view at once; code uses <see cref="Get(string)"/>.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _current = Strings.For(Languages.Default);

    public static string Language { get; private set; } = Languages.Default;

    public static event Action? Changed;

    public static void SetLanguage(string code)
    {
        Language = Languages.Get(code).Code;
        _current = Strings.For(Language);
        ApplyTo(Application.Current);
        Changed?.Invoke();
    }

    public static void ApplyTo(Application? app)
    {
        if (app == null) return;
        foreach (var key in Strings.For(Languages.Default).Keys) app.Resources["T_" + key] = Get(key);
    }

    /// <summary>The text for a key in the current language; English when a translation lacks it, the key itself as a last resort.</summary>
    public static string Get(string key) =>
        _current.TryGetValue(key, out var text) ? text
        : Strings.For(Languages.Default).TryGetValue(key, out var english) ? english
        : key;

    public static string Format(string key, params object?[] args) => string.Format(Get(key), args);
}

/// <summary>Base for view models that expose localised text: all their bindings refresh when the language changes.</summary>
public abstract class LocalizedObject : ObservableObject
{
    protected LocalizedObject() => Loc.Changed += () => OnPropertyChanged(string.Empty);
}
