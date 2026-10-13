using System.Text.RegularExpressions;
using PdfModifier.Core.Text;
using PdfModifier.Localization;

namespace PdfModifier.Tests;

public partial class LocalizationTests
{
    public static IEnumerable<object[]> Translations => Languages.All.Where(l => l.Code != "en").Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryLanguageHasExactlyTheEnglishKeys(string language)
    {
        var translated = Strings.For(language);
        Assert.NotSame(Strings.English, translated);
        Assert.Empty(Strings.English.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(Strings.English.Keys));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void PlaceholdersMatchTheEnglishText(string language)
    {
        var translated = Strings.For(language);
        var broken = Strings.English
            .Where(pair => !Placeholders(pair.Value).SetEquals(Placeholders(translated[pair.Key])))
            .Select(pair => pair.Key);
        Assert.Empty(broken);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TextsAreActuallyTranslated(string language)
    {
        // A few words are legitimately the same (OK, PDF, Position…), but the bulk must differ from English.
        var translated = Strings.For(language);
        var same = Strings.English.Count(pair => translated[pair.Key] == pair.Value);
        Assert.True(same < Strings.English.Count * 0.08, $"{same} of {Strings.English.Count} texts equal the English ones");
        Assert.All(translated.Values, text => Assert.False(string.IsNullOrWhiteSpace(text)));
    }

    /// <summary>
    /// A text written twice under one name is no error to the compiler: the later one silently takes the place of
    /// the earlier, and something in the window says the wrong thing. So the files themselves are looked through.
    /// </summary>
    [Fact]
    public void NoTextIsWrittenTwiceUnderOneName()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "PdfModifier.sln"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException();
        var files = Directory.GetFiles(Path.Combine(root, "PdfModifier", "Localization"), "Strings*.cs");
        Assert.Equal(Languages.All.Count(), files.Length);

        var twice = files.SelectMany(file => EntryKey().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value)
            .GroupBy(key => key).Where(group => group.Count() > 1).Select(group => $"{Path.GetFileName(file)}: {group.Key}"));

        Assert.Empty(twice);
    }

    [Fact]
    public void EveryKeyUsedByTheAppExists()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "PdfModifier.sln"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException();
        var app = Path.Combine(root, "PdfModifier");
        var used = new HashSet<string>();
        var quoted = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(app, "*.axaml", SearchOption.AllDirectories))
            foreach (Match m in ResourceKey().Matches(File.ReadAllText(file)))
                used.Add(m.Groups[1].Value);

        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || Path.GetFileName(file).StartsWith("Strings")) continue;
            var source = File.ReadAllText(file);
            foreach (Match m in CodeKey().Matches(source)) used.Add(m.Groups[1].Value);
            foreach (Match m in QuotedWord().Matches(source)) quoted.Add(m.Groups[1].Value);
            foreach (Match m in ChoiceKey().Matches(source)) used.Add(m.Groups[1].Value);
            foreach (Match m in ToolKey().Matches(source))
            {
                used.Add("Tool" + m.Groups[1].Value);
                used.Add("Tool" + m.Groups[1].Value + "Hint");
                used.Add(m.Groups[2].Value);
            }
        }

        Assert.True(used.Count > 200, "the scan should find the keys in use");
        Assert.Empty(used.Where(key => !Strings.English.ContainsKey(key)).Order());
        // And the other way round: nothing is translated eight times without being shown anywhere. Keys can also be
        // picked in expressions (a ? "X" : "Y"), so any quoted occurrence in the sources counts as a use here.
        Assert.Empty(Strings.English.Keys.Where(key => !used.Contains(key) && !quoted.Contains(key)).Order());
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).ToHashSet();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"""(\w+)""")]
    private static partial Regex QuotedWord();

    // "T_Key" as a resource name, but not the tail of something like PART_ContentPresenter.
    [GeneratedRegex(@"(?<!\w)T_(\w+)")]
    private static partial Regex ResourceKey();

    // Loc.Get("X"), Loc.Format("X", SetKey("X"), and field constructors, whose first argument is the label key.
    [GeneratedRegex(@"(?:Loc\.Get|Loc\.Format|SetKey|new (?:Text|Number|Slider|Bool|Toggle|Choice|Mode|Pages|File|FileList|Action|CheckList|Color|PieceList|Folder|Place|List)Field)\(""(\w+)""[,)]")]
    private static partial Regex CodeKey();

    // An entry of one of the dictionaries: ["Key"] = "…" at the start of a line.
    [GeneratedRegex(@"^\s*\[""(\w+)""\]\s*=", RegexOptions.Multiline)]
    private static partial Regex EntryKey();

    // new Choice("key", "LabelKey") — but not the literal form new Choice("key", "Text", true).
    [GeneratedRegex(@"new Choice\((?:""[^""]*""|nameof\([^)]*\)), ""(\w+)""\)")]
    private static partial Regex ChoiceKey();

    [GeneratedRegex(@"base\(""(\w+)"", ""(\w+)""\)")]
    private static partial Regex ToolKey();
}
