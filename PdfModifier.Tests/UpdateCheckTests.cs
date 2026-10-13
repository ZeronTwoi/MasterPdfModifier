using Avalonia.Headless.XUnit;
using PdfModifier.Localization;
using PdfModifier.Services;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// Being told about new versions is for those who asked for it. The tests stand in for GitHub and count what it was
/// asked, which for everyone else has to be nothing.
/// </summary>
public class UpdateCheckTests
{
    private const string Newest = "https://github.com/ZeronTwoi/MasterPdfModifier/releases/tag/v";

    /// <summary>A check whose questions are answered here, and a list of what was asked.</summary>
    private static (UpdateCheck Check, List<string> Asked) Answering(params string?[] answers)
    {
        var asked = new List<string>();
        var check = new UpdateCheck
        {
            Redirect = (address, _) =>
            {
                asked.Add(address);
                return Task.FromResult(asked.Count <= answers.Length ? answers[asked.Count - 1] : null);
            }
        };
        return (check, asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task WithoutAYesNoConnectionIsEverTried(bool? answer)
    {
        var (check, asked) = Answering(Newest + "9.0.0");

        // Declined, or not asked yet: however often the app comes by, nothing goes out.
        for (var start = 0; start < 3; start++)
            Assert.Null(await check.LookOnce(new Settings { UpdateNotice = answer }, "0.1.0"));

        Assert.Empty(asked);
    }

    [Fact]
    public async Task WithAYesTheAppLooksOnceAndRemembersWhatItFound()
    {
        var (check, asked) = Answering(Newest + "0.2.0");
        var settings = new Settings { UpdateNotice = true };

        Assert.Equal("0.2.0", await check.LookOnce(settings, "0.1.0"));
        // Asked again while the app runs, it answers from memory.
        Assert.Equal("0.2.0", await check.LookOnce(settings, "0.1.0"));

        Assert.Equal([AppInfo.LatestVersionPage], asked);
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.10.0", "0.9.1", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("nightly", "0.1.0", false)]
    public async Task OnlyAVersionNewerThanThisOneIsNews(string published, string current, bool news)
    {
        var (check, _) = Answering(Newest + published);

        var found = await check.LookOnce(new Settings { UpdateNotice = true }, current);

        Assert.Equal(news ? published : null, found);
    }

    [Fact]
    public async Task ARenamedRepositoryIsFollowedButOnlyOnGitHub()
    {
        var (moved, askedOfMoved) = Answering("https://github.com/Someone/NewName/releases/latest", "https://github.com/Someone/NewName/releases/tag/v0.3.0");
        Assert.Equal("0.3.0", await moved.LookOnce(new Settings { UpdateNotice = true }, "0.1.0"));
        Assert.Equal(2, askedOfMoved.Count);

        var (elsewhere, askedElsewhere) = Answering("https://example.org/releases/tag/v9.9.9", Newest + "9.9.9");
        Assert.Null(await elsewhere.LookOnce(new Settings { UpdateNotice = true }, "0.1.0"));
        Assert.Single(askedElsewhere);
    }

    [Fact]
    public async Task BeingOfflineIsNotWorthAWord()
    {
        var check = new UpdateCheck { Redirect = (_, _) => throw new HttpRequestException("no network") };
        Assert.Null(await check.LookOnce(new Settings { UpdateNotice = true }, "0.1.0"));

        // No release yet, or a repository that cannot be seen: GitHub does not redirect, and that is all.
        var (nothing, asked) = Answering((string?)null);
        Assert.Null(await nothing.LookOnce(new Settings { UpdateNotice = true }, "0.1.0"));
        Assert.Single(asked);
    }

    /// <summary>
    /// Asks GitHub for real, about a project that has releases: one request for headers, answered with the address of
    /// its newest version. Only when PDFMODIFIER_NETWORK is set.
    /// </summary>
    [Fact]
    public async Task GitHubAnswersTheQuestionWithARedirectToTheNewestVersion()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDFMODIFIER_NETWORK"))) return;
        var target = await new UpdateCheck().Redirect("https://github.com/tesseract-ocr/tesseract/releases/latest", CancellationToken.None);

        Assert.NotNull(target);
        Assert.NotNull(UpdateCheck.VersionIn(target));
    }

    [AvaloniaFact]
    public async Task ANewerVersionShowsAsAWarningSignOnTheSettingsButtonAndAsANoteInTheSettings()
    {
        var (window, vm) = Start();
        vm.Settings.UpdateNotice = true;
        vm.Updates.Redirect = (_, _) => Task.FromResult<string?>(Newest + "9.4.0");
        using var scan = TestPdf.FakeScan(1240, 1754);
        vm.Session.Open(TestPdf.FromImage(scan, 150), null);
        await Settle();
        Assert.False(vm.HasNewVersion);

        await vm.LookForNewVersion();
        await Settle();

        Assert.True(vm.HasNewVersion);
        Assert.Equal(string.Format(Strings.English["UpdateAvailable"], "9.4.0", AppInfo.Version), vm.SettingsTip);
        Capture(window, "41-update-sign");

        var settings = Tool<SettingsTool>(vm);
        await Settle();
        var note = settings.Fields.OfType<InfoField>().First();
        Assert.True(note.Warns);
        Assert.Contains("9.4.0", note.Text);
        Assert.True(Field<ActionField>(settings, "UpdateGet").IsVisible);
        Assert.True(Field<BoolField>(settings, "UpdateNotify").Value);
        Capture(window, "42-update-in-settings");

        // Clearing the box is a no: it is kept, and the next start of the app asks nothing of anyone.
        Field<BoolField>(settings, "UpdateNotify").Value = false;
        Assert.False(vm.Settings.UpdateNotice);
        var asked = 0;
        var later = new UpdateCheck
        {
            Redirect = (_, _) =>
            {
                asked++;
                return Task.FromResult<string?>(Newest + "9.4.0");
            }
        };
        Assert.Null(await later.LookOnce(vm.Settings, AppInfo.Version));
        Assert.Equal(0, asked);
        window.Close();
    }

    [Theory]
    [InlineData("tr-TR", "tr")]
    [InlineData("pt-BR", "pt")]
    [InlineData("de-AT", "de")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-GB", "en")]
    [InlineData("zh-CN", "en")]
    public void BeforeAnythingIsChosenTheAppSpeaksTheLanguageOfTheComputerIfItCan(string culture, string language) =>
        Assert.Equal(language, Settings.LanguageOf(new System.Globalization.CultureInfo(culture)));

    [AvaloniaFact]
    public async Task WithoutNewsTheSettingsShowNoSign()
    {
        var (window, vm) = Start();
        vm.Settings.UpdateNotice = true;
        vm.Updates.Redirect = (_, _) => Task.FromResult<string?>(Newest + AppInfo.Version);
        await vm.LookForNewVersion();
        var settings = Tool<SettingsTool>(vm);
        await Settle();

        Assert.False(vm.HasNewVersion);
        Assert.Equal(Strings.English["ToolSettings"], vm.SettingsTip);
        Assert.False(settings.Fields.OfType<InfoField>().First().HasText);
        Assert.False(Field<ActionField>(settings, "UpdateGet").IsVisible);
        window.Close();
    }
}
