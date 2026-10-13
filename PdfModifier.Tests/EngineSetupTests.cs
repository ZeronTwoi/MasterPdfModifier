using Avalonia.Headless.XUnit;
using PdfModifier.Core.Ocr;
using PdfModifier.Services;

namespace PdfModifier.Tests;

/// <summary>
/// What the app does so that nobody has to install anything by hand: finding out what text recognition lacks on a
/// computer, and putting itself into the application menu on Linux.
/// </summary>
public class EngineSetupTests
{
    [Theory]
    [InlineData("apt-get", "tesseract-ocr")]
    [InlineData("dnf", "tesseract")]
    [InlineData("zypper", "tesseract-ocr")]
    [InlineData("pacman", "tesseract")]
    public void TesseractIsInstalledThroughThePackageManagerThatIsThere(string manager, string package)
    {
        var command = LinuxPackages.ForTesseract(program => program == "pkexec" || program == manager);

        Assert.NotNull(command);
        // Started through pkexec, so that the desktop asks for the password in a window of its own.
        Assert.Equal("pkexec", command.Program);
        Assert.Contains(manager, command.Arguments);
        Assert.Contains(package, command.Arguments);
    }

    [Fact]
    public void NothingIsInstalledWhereThereIsNoWayToAskForPermissionOrNoKnownPackageManager()
    {
        Assert.Null(LinuxPackages.ForTesseract(program => program == "apt-get"));
        Assert.Null(LinuxPackages.ForTesseract(program => program == "pkexec"));
    }

    [Fact]
    public void TheLanguagesAnInstalledTesseractBringsAreRead()
    {
        const string listing = "List of available languages in \"/usr/share/tesseract-ocr/5/tessdata/\" (3):\neng\r\nosd\ndeu\n\n";

        Assert.Equal(["deu", "eng", "osd"], TesseractCli.ParseLanguageList(listing).Order());
    }

    [Fact]
    public void TheRuntimeCountsAsThereOnlyWhenAllOfItIs()
    {
        Assert.True(VcRuntime.IsInstalled(_ => true));
        Assert.False(VcRuntime.IsInstalled(library => library != "vcruntime140_1.dll"));
        Assert.False(VcRuntime.IsInstalled(_ => false));
    }

    [Fact]
    public void AProgramFromTheInternetIsTrustedOnlyWithMicrosoftsSignature()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The program running these tests is Microsoft's and signed; the tests themselves are neither.
        Assert.True(VcRuntime.IsFromMicrosoft(Environment.ProcessPath!));
        Assert.False(VcRuntime.IsFromMicrosoft(typeof(EngineSetupTests).Assembly.Location));
        var text = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N") + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(text)!);
        File.WriteAllText(text, "not a program");
        Assert.False(VcRuntime.IsFromMicrosoft(text));
        File.Delete(text);
    }

    /// <summary>
    /// Fetches Microsoft's installer for real and checks its signature, without running it. Only when asked for by
    /// setting PDFMODIFIER_NETWORK, as it downloads some 25 MB.
    /// </summary>
    [Fact]
    public async Task TheInstallerOfTheRuntimeComesFromMicrosoft()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDFMODIFIER_NETWORK"))) return;
        var installer = await VcRuntime.DownloadAsync();
        try
        {
            Assert.True(new FileInfo(installer).Length > 5_000_000);
            Assert.True(VcRuntime.IsFromMicrosoft(installer));
        }
        finally
        {
            File.Delete(installer);
        }
    }

    [Theory]
    [InlineData("/opt/app/MasterPdfModifier", "\"/opt/app/MasterPdfModifier\"")]
    [InlineData("/home/a b/My Apps/MasterPdfModifier", "\"/home/a b/My Apps/MasterPdfModifier\"")]
    [InlineData("/home/a/$x/100%/it's \"here\"", "\"/home/a/\\\\$x/100%%/it's \\\\\"here\\\\\"\"")]
    public void AMenuEntryNamesTheProgramTheWaySuchEntriesWantIt(string path, string quoted) =>
        Assert.Equal(quoted, DesktopEntry.Quote(path));

    [AvaloniaFact]
    public void TheMenuEntryIsWrittenFollowsTheProgramAndIsTakenOutAgain()
    {
        var original = DesktopEntry.DataFolder;
        var folder = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"));
        DesktopEntry.DataFolder = folder;
        try
        {
            Assert.False(DesktopEntry.IsOwn);
            DesktopEntry.Add("/home/someone/Downloads/MasterPdfModifier");

            var entry = Path.Combine(folder, "applications", "masterpdfmodifier.desktop");
            var text = File.ReadAllText(entry);
            Assert.StartsWith("[Desktop Entry]\n", text);
            Assert.Contains("\nName=" + AppInfo.Name + "\n", text);
            Assert.Contains("\nExec=\"/home/someone/Downloads/MasterPdfModifier\" %F\n", text);
            Assert.Contains("\nIcon=masterpdfmodifier\n", text);
            Assert.Contains("\nMimeType=application/pdf;\n", text);
            // How the desktop tells that a window belongs to this entry.
            Assert.Contains("\nStartupWMClass=" + AppInfo.WindowClass + "\n", text);
            // Linux takes a Windows line ending for part of the line.
            Assert.DoesNotContain("\r", text);
            Assert.True(new FileInfo(Path.Combine(folder, "icons", "hicolor", "256x256", "apps", "masterpdfmodifier.png")).Length > 1000);
            Assert.StartsWith("<svg", File.ReadAllText(Path.Combine(folder, "icons", "hicolor", "scalable", "apps", "masterpdfmodifier.svg")));
            Assert.True(DesktopEntry.IsOwn);

            // The program is one file: it gets moved, or downloaded again into another folder.
            DesktopEntry.KeepUp("/opt/elsewhere/MasterPdfModifier");
            Assert.Contains("\nExec=\"/opt/elsewhere/MasterPdfModifier\" %F\n", File.ReadAllText(entry));

            DesktopEntry.Remove();
            Assert.False(DesktopEntry.IsOwn);
            Assert.Empty(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
        }
        finally
        {
            DesktopEntry.DataFolder = original;
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheAppCanCheckItselfWithoutOpeningAWindow()
    {
        var report = new StringWriter();

        Assert.Equal(0, SelfCheck.Run(report));

        var text = report.ToString();
        Assert.StartsWith(AppInfo.Name, text);
        Assert.Contains("ok    Imaging", text);
        Assert.Contains("ok    PDF engine", text);
        Assert.Contains("Text recognition", text);
        Assert.DoesNotContain("FAIL", text);
    }
}
