namespace PdfModifier.Core.Text;

/// <summary>A language the app supports, both for its interface and for text recognition.</summary>
public sealed record Language(string Code, string NativeName, string TesseractCode);

public static class Languages
{
    public const string Default = "en";

    public static readonly IReadOnlyList<Language> All =
    [
        new("en", "English", "eng"),
        new("es", "Español", "spa"),
        new("de", "Deutsch", "deu"),
        new("ru", "Русский", "rus"),
        new("fr", "Français", "fra"),
        new("ja", "日本語", "jpn"),
        new("pt", "Português", "por"),
        new("tr", "Türkçe", "tur")
    ];

    public static Language Get(string code) =>
        All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static bool IsSupported(string code) =>
        All.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}
