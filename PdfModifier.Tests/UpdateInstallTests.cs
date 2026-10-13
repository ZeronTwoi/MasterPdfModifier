using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using PdfModifier.Core.Ocr;
using PdfModifier.Localization;
using PdfModifier.Services;
using PdfModifier.ViewModels;
using PdfModifier.ViewModels.Tools;
using static PdfModifier.Tests.UiTests;

namespace PdfModifier.Tests;

/// <summary>
/// The button that puts a newer version in the place of the running one, and the tidying up after it. The tests
/// stand in for GitHub and for starting programs: what would be fetched is written by them, what would be started
/// is only noted.
/// </summary>
public sealed class UpdateInstallTests : IDisposable
{
    private const string Page = "https://github.com/ZeronTwoi/MasterPdfModifier/releases/tag/v9.4.0";
    private const string Downloads = "https://github.com/ZeronTwoi/MasterPdfModifier/releases/download/v9.4.0/";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PdfModifier.Tests", Guid.NewGuid().ToString("N"));

    public UpdateInstallTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    private string In(params string[] parts) => Path.Combine([_folder, .. parts]);

    private string Write(string name, string content)
    {
        var path = In(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sum(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// An updater for a copy of the app of the given kind, with a GitHub that publishes the given download and the
    /// given list of checksums. It notes what was fetched and what was started.
    /// </summary>
    private (Updater Updater, List<string> Fetched, List<ProcessStartInfo> Started) UpdaterFor(InstallKind kind, bool windows, string program,
        string published, string? checksums = null)
    {
        var fetched = new List<string>();
        var started = new List<ProcessStartInfo>();
        var asset = Updater.AssetFor(kind, windows)!;
        var updater = new Updater
        {
            Installed = new Installation(kind, program),
            Windows = windows,
            Folder = In("downloads"),
            Running = _ => 1,
            Fetch = (address, target, progress, _) =>
            {
                fetched.Add(address);
                File.WriteAllText(target, address.EndsWith(Updater.Checksums, StringComparison.Ordinal) ? checksums ?? $"{Sum(published)}  {asset}\n" : published);
                progress?.Report(1);
                return Task.CompletedTask;
            },
            Start = info =>
            {
                started.Add(info);
                return true;
            }
        };
        return (updater, fetched, started);
    }

    [Theory]
    [InlineData(InstallKind.Setup, true, "MasterPdfModifier-Setup.exe")]
    [InlineData(InstallKind.OneFile, true, "MasterPdfModifier.exe")]
    [InlineData(InstallKind.OneFile, false, "MasterPdfModifier-Linux.tar.gz")]
    [InlineData(InstallKind.Package, false, "MasterPdfModifier.deb")]
    [InlineData(InstallKind.Unknown, true, null)]
    [InlineData(InstallKind.Unknown, false, null)]
    [InlineData(InstallKind.Package, true, null)]
    [InlineData(InstallKind.Setup, false, null)]
    public void EveryKindOfDownloadIsUpdatedFromTheSameKind(InstallKind kind, bool windows, string? asset)
    {
        Assert.Equal(asset, Updater.AssetFor(kind, windows));
        Assert.Equal(asset != null, new Updater { Installed = new Installation(kind, "anywhere"), Windows = windows }.Asset != null);
    }

    [Fact]
    public void HowTheAppCameOntoTheComputerIsToldFromWhereItsProgramLies()
    {
        // Put there by the setup: the program that removes it again lies next to it.
        var installed = Write(Path.Combine("installed", "MasterPdfModifier.exe"), "program");
        Write(Path.Combine("installed", Installation.Uninstaller), "uninstaller");
        Assert.Equal(InstallKind.Setup, Installation.Find(installed, In("installed"), true).Kind);

        // One file: it runs from where it unpacked itself, which is not where it lies.
        var single = Write(Path.Combine("desk", "MasterPdfModifier.exe"), "program");
        Assert.Equal(InstallKind.OneFile, Installation.Find(single, In("temp", ".net", "MasterPdfModifier", "abc"), true).Kind);
        Assert.Equal(InstallKind.OneFile, Installation.Find(single, In("home", ".net", "MasterPdfModifier", "abc"), false).Kind);

        // A build from the source has its parts next to it and is none of the downloads; a trailing separator on
        // the folder is no difference.
        Assert.Equal(InstallKind.Unknown, Installation.Find(single, In("desk") + Path.DirectorySeparatorChar, true).Kind);
        Assert.Equal(InstallKind.Unknown, Installation.Find(null, In("desk"), true).Kind);

        // The Linux package puts the program in one known place.
        Assert.Equal(InstallKind.Package, Installation.Find(Installation.PackageProgram, "/opt/masterpdfmodifier/", false).Kind);
        Assert.NotEqual(InstallKind.Package, Installation.Find(Installation.PackageProgram, "/opt/masterpdfmodifier/", true).Kind);
    }

    [Fact]
    public void TheDownloadsOfAVersionAreFoundFromItsPage()
    {
        Assert.Equal(Downloads + "MasterPdfModifier-Setup.exe", Updater.DownloadAddress(Page, "MasterPdfModifier-Setup.exe"));
        // Where GitHub pointed after the project moved is where its downloads are.
        Assert.Equal("https://github.com/Someone/NewName/releases/download/v1.2/SHA256SUMS.txt",
            Updater.DownloadAddress("https://github.com/Someone/NewName/releases/tag/v1.2", Updater.Checksums));
        Assert.Throws<ArgumentException>(() => Updater.DownloadAddress("https://github.com/ZeronTwoi/MasterPdfModifier/releases/latest", "x"));
    }

    [Theory]
    [InlineData("https://github.com/ZeronTwoi/MasterPdfModifier/releases/download/v1/x.exe", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("http://github.com/ZeronTwoi/MasterPdfModifier/releases/download/v1/x.exe", false)]
    [InlineData("https://github.com.example.org/x.exe", false)]
    [InlineData("https://notgithubusercontent.com/x.exe", false)]
    [InlineData("https://example.org/github.com/x.exe", false)]
    public void ADownloadIsOnlyFollowedToAddressesOfGitHub(string address, bool trusted) =>
        Assert.Equal(trusted, Updater.IsGitHub(new Uri(address)));

    [Fact]
    public void TheChecksumOfADownloadIsReadFromThePublishedList()
    {
        var one = new string('a', 64);
        var other = new string('B', 64);
        var list = $"{one}  MasterPdfModifier-Setup.exe\r\n{other} *MasterPdfModifier.exe\nnot a line\n{new string('z', 64)}  Broken.exe\n";

        Assert.Equal(one, Updater.ChecksumIn(list, "MasterPdfModifier-Setup.exe"));
        Assert.Equal(other.ToLowerInvariant(), Updater.ChecksumIn(list, "MasterPdfModifier.exe"));
        // A name that only ends the same, a file that is not listed, a checksum that is none.
        Assert.Null(Updater.ChecksumIn(list, "Setup.exe"));
        Assert.Null(Updater.ChecksumIn(list, "MasterPdfModifier.deb"));
        Assert.Null(Updater.ChecksumIn(list, "Broken.exe"));
    }

    [Fact]
    public async Task TheDownloadIsFetchedCheckedAndNotFetchedTwice()
    {
        var (updater, fetched, started) = UpdaterFor(InstallKind.Setup, true, In("app", "MasterPdfModifier.exe"), "the new setup");

        var ready = await updater.Prepare(Page, "9.4.0");

        Assert.Equal("9.4.0", ready.Version);
        Assert.Equal("the new setup", File.ReadAllText(ready.File));
        Assert.Equal("MasterPdfModifier-Setup.exe", Path.GetFileName(ready.File));
        Assert.StartsWith(In("downloads"), ready.File);
        Assert.Equal([Downloads + Updater.Checksums, Downloads + "MasterPdfModifier-Setup.exe"], fetched);
        // Getting it ready starts nothing.
        Assert.Empty(started);

        // Pressed again: the list is asked for once more, the file that is there and right is not.
        await updater.Prepare(Page, "9.4.0");
        Assert.Equal(3, fetched.Count);
        Assert.Equal(Downloads + Updater.Checksums, fetched[2]);
    }

    [Fact]
    public async Task ADownloadThatIsNotWhatWasPublishedIsDeletedAndNeverStarted()
    {
        var program = In("app", "MasterPdfModifier.exe");
        var (tampered, _, started) = UpdaterFor(InstallKind.Setup, true, program, "something else", $"{Sum("the new setup")}  MasterPdfModifier-Setup.exe\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => tampered.Prepare(Page, "9.4.0"));
        Assert.False(File.Exists(In("downloads", "9.4.0", "MasterPdfModifier-Setup.exe")));
        Assert.Empty(started);

        // A version published without a checksum for this download is not taken on trust either.
        var (unlisted, fetched, _) = UpdaterFor(InstallKind.Setup, true, program, "the new setup", $"{Sum("the new setup")}  MasterPdfModifier.deb\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => unlisted.Prepare(Page, "9.4.0"));
        Assert.Equal([Downloads + Updater.Checksums], fetched);
    }

    [Fact]
    public async Task AnInstalledAppHandsOverToTheNewSetupWhichAsksNothing()
    {
        var (updater, _, started) = UpdaterFor(InstallKind.Setup, true, In("app", "MasterPdfModifier.exe"), "the new setup");
        var ready = await updater.Prepare(Page, "9.4.0");

        await updater.Apply(ready, @"C:\My Documents\letter one.pdf");

        var setup = Assert.Single(started);
        Assert.Equal(ready.File, setup.FileName);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /UPDATE=1 /REOPEN=\"C:\\My Documents\\letter one.pdf\"", setup.Arguments);

        // Without a document there is nothing to open again.
        await updater.Apply(ready, null);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /UPDATE=1", started[1].Arguments);
    }

    [Fact]
    public async Task TheOneFileAppOnWindowsStepsAsideForItsSuccessorWhichClearsItAway()
    {
        var program = Write(Path.Combine("desk", "MasterPdfModifier.exe"), "the old program");
        var (updater, _, started) = UpdaterFor(InstallKind.OneFile, true, program, "the new program");
        var ready = await updater.Prepare(Page, "9.4.0");

        await updater.Apply(ready, "letter.pdf");

        Assert.Equal("the new program", File.ReadAllText(program));
        Assert.Equal("the old program", File.ReadAllText(program + Updater.SteppedAside));
        var again = Assert.Single(started);
        Assert.Equal(program, again.FileName);
        Assert.Equal(["letter.pdf"], again.ArgumentList);

        // The new one, when it starts: the old program goes, and so does what the update was made from. Another
        // window being open does not keep the old program, which is nobody's any more.
        var installed = new Installation(InstallKind.OneFile, program);
        Assert.Equal([program + Updater.SteppedAside], Leftovers.Find(installed, In("unpacked"), In("scratch"), In("downloads"), alone: false));
        var stale = Leftovers.Find(installed, In("unpacked"), In("scratch"), In("downloads"), alone: true);
        Assert.Equal([program + Updater.SteppedAside, In("downloads")], stale);
        Leftovers.Remove(stale, TimeSpan.Zero);
        Assert.Equal(["MasterPdfModifier.exe"], Directory.EnumerateFileSystemEntries(In("desk")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(In("downloads")));
    }

    [Fact]
    public async Task AFolderThatCannotTakeTheNewProgramKeepsTheOldOne()
    {
        // A program that is not there stands for a folder that cannot be written to: nothing is moved, nothing started.
        var (updater, _, started) = UpdaterFor(InstallKind.OneFile, true, In("gone", "MasterPdfModifier.exe"), "the new program");
        var ready = await updater.Prepare(Page, "9.4.0");

        await Assert.ThrowsAnyAsync<IOException>(() => updater.Apply(ready, null));

        Assert.Empty(started);
        Assert.True(File.Exists(ready.File));
    }

    [Fact]
    public async Task TheOneFileAppOnLinuxIsTakenOutOfTheArchiveAndPutInPlace()
    {
        var program = Write(Path.Combine("apps", "MasterPdfModifier"), "the old program");
        var (updater, _, started) = UpdaterFor(InstallKind.OneFile, false, program, "");
        var archive = In("MasterPdfModifier-Linux.tar.gz");
        WriteArchive(archive, ("LICENSE.txt", "the licence"), ("MasterPdfModifier", "the new program"), ("THIRD-PARTY-NOTICES.md", "the notices"));

        await updater.Apply(new ReadyUpdate("9.4.0", archive), null);

        Assert.Equal("the new program", File.ReadAllText(program));
        Assert.Equal(["MasterPdfModifier"], Directory.EnumerateFileSystemEntries(In("apps")).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(program, Assert.Single(started).FileName);

        // An archive without the program in it leaves the one that is there alone.
        WriteArchive(archive, ("LICENSE.txt", "the licence"));
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.Apply(new ReadyUpdate("9.4.1", archive), null));
        Assert.Equal("the new program", File.ReadAllText(program));
    }

    [Fact]
    public async Task ThePackageIsInstalledThroughThePackageManagerAndTheAppStartedAgain()
    {
        var (updater, _, started) = UpdaterFor(InstallKind.Package, false, Installation.PackageProgram, "the new package");
        var installed = new List<string>();
        updater.InstallPackage = (file, _) =>
        {
            installed.Add(file);
            return Task.FromResult(true);
        };
        var ready = await updater.Prepare(Page, "9.4.0");

        await updater.Apply(ready, "/home/someone/letter.pdf");

        Assert.Equal([ready.File], installed);
        Assert.Equal(Installation.PackageProgram, Assert.Single(started).FileName);
        Assert.Equal(["/home/someone/letter.pdf"], started[0].ArgumentList);

        // The password was not given, or the package manager said no: the app that is running stays.
        updater.InstallPackage = (_, _) => Task.FromResult(false);
        await Assert.ThrowsAsync<IOException>(() => updater.Apply(ready, null));
        Assert.Single(started);
    }

    [Fact]
    public void ADownloadedPackageIsInstalledWithThePasswordAskedInAWindow()
    {
        var apt = LinuxPackages.ForPackageFile("/home/a/MasterPdfModifier.deb", program => program is "pkexec" or "apt-get" or "dpkg");
        Assert.NotNull(apt);
        Assert.Equal("pkexec", apt.Program);
        Assert.Equal(["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "/home/a/MasterPdfModifier.deb"], apt.Arguments);

        var dpkg = LinuxPackages.ForPackageFile("/home/a/MasterPdfModifier.deb", program => program is "pkexec" or "dpkg");
        Assert.Equal(["dpkg", "-i", "/home/a/MasterPdfModifier.deb"], dpkg!.Arguments);

        Assert.Null(LinuxPackages.ForPackageFile("/home/a/MasterPdfModifier.deb", program => program is "apt-get" or "dpkg"));
        Assert.Null(LinuxPackages.ForPackageFile("/home/a/MasterPdfModifier.deb", program => program == "pkexec"));
    }

    [Fact]
    public void WhatEarlierRunsAndVersionsLeftBehindIsClearedAwayWhenNoOtherWindowIsOpen()
    {
        // The undo history of a run that crashed, pages that were dragged out, an installer that was fetched.
        Write(Path.Combine("temp", "PdfModifier", "0123abcd", "000001.pdf"), "an earlier state");
        Write(Path.Combine("temp", "PdfModifier", "pages-4567", "1", "page.png"), "a page");
        Write(Path.Combine("temp", "PdfModifier", "setup", "vc_redist.x64.exe"), "an installer");
        // What two older versions of the one-file app unpacked, next to what this one runs from.
        Write(Path.Combine("temp", ".net", "MasterPdfModifier", "older", "MasterPdfModifier.dll"), "old");
        Write(Path.Combine("temp", ".net", "MasterPdfModifier", "oldest", "x64", "tesseract.dll"), "older");
        Write(Path.Combine("temp", ".net", "MasterPdfModifier", "mine", "MasterPdfModifier.dll"), "mine");
        // And things that are not the app's: they are never on the list.
        Write(Path.Combine("temp", ".net", "AnotherApp", "abc", "Another.dll"), "theirs");
        Write(Path.Combine("temp", "PdfModifier.Tests", "x.txt"), "theirs");
        var program = Write(Path.Combine("desk", "MasterPdfModifier.exe"), "program");
        var single = new Installation(InstallKind.OneFile, program);
        var mine = In("temp", ".net", "MasterPdfModifier", "mine") + Path.DirectorySeparatorChar;
        var scratch = In("temp", "PdfModifier");

        // With another window open nothing is touched: any of it could be in use there.
        Assert.Empty(Leftovers.Find(single, mine, scratch, In("downloads"), alone: false));

        var stale = Leftovers.Find(single, mine, scratch, In("downloads"), alone: true);
        Assert.Equal(new[] { "0123abcd", "pages-4567", "setup", "older", "oldest" }.Order(), stale.Select(Path.GetFileName).Order());
        Leftovers.Remove(stale, TimeSpan.Zero);

        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        Assert.Equal(["mine"], Directory.EnumerateFileSystemEntries(In("temp", ".net", "MasterPdfModifier")).Select(Path.GetFileName));
        Assert.True(File.Exists(In("temp", ".net", "AnotherApp", "abc", "Another.dll")));
        Assert.True(File.Exists(In("temp", "PdfModifier.Tests", "x.txt")));

        // An installed app unpacks nothing; the folders next to the one it runs from are other programs' business.
        var installed = new Installation(InstallKind.Setup, program);
        Write(Path.Combine("programs", "Another App", "another.exe"), "theirs");
        Assert.Empty(Leftovers.Find(installed, In("programs", "Master PDF Modifier"), scratch, In("downloads"), alone: true));
        // Nor does a one-file app clear anything away when it runs from a folder that is not laid out as expected.
        Assert.Empty(Leftovers.OtherUnpackedVersions(In("programs", "Master PDF Modifier")));
        Assert.Empty(Leftovers.OtherUnpackedVersions(In("temp", ".net", "MasterPdfModifier")));
    }

    [Fact]
    public void AFolderThatIsStillInUseIsLeftWholeAndTriedAgain()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Write(Path.Combine("downloads", "9.4.0", "MasterPdfModifier-Setup.exe"), "the setup, still ending");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Leftovers.Remove([In("downloads")], TimeSpan.Zero, attempts: 2);
            Assert.True(File.Exists(file));
        }

        Leftovers.Remove([In("downloads")], TimeSpan.Zero, attempts: 2);
        Assert.False(Directory.Exists(In("downloads")));
    }

    /// <summary>
    /// Fetches a small file from a release at GitHub for real, by the way a download goes: GitHub passes it on to
    /// the server that has it. Only when PDFMODIFIER_NETWORK is set.
    /// </summary>
    [Fact]
    public async Task GitHubHandsOutTheFilesOfAVersion()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDFMODIFIER_NETWORK"))) return;
        var target = In("checksums.txt");
        var steps = new List<double>();

        await Updater.FromGitHub("https://github.com/cli/cli/releases/download/v2.40.0/gh_2.40.0_checksums.txt", target, new Progress<double>(steps.Add),
            CancellationToken.None);

        Assert.Contains("gh_2.40.0_windows_amd64.zip", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".part"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Updater.FromGitHub("https://example.org/file", In("other"), null, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task PressingUpdateFetchesTheNewVersionHandsOverAndClosesTheApp()
    {
        var (window, vm) = Start();
        vm.Settings.UpdateNotice = true;
        vm.Updates.Redirect = (_, _) => Task.FromResult<string?>(Page);
        var (updater, fetched, started) = UpdaterFor(InstallKind.Setup, true, In("app", "MasterPdfModifier.exe"), "the new setup");
        (vm.Updater.Installed, vm.Updater.Windows, vm.Updater.Folder) = (updater.Installed, true, updater.Folder);
        (vm.Updater.Fetch, vm.Updater.Start, vm.Updater.Running) = (updater.Fetch, updater.Start, _ => 2);
        var closed = 0;
        vm.Quit = () => closed++;
        await vm.LookForNewVersion();
        var settings = Tool<SettingsTool>(vm);
        await Settle();
        var note = settings.Fields.OfType<InfoField>().First();
        Assert.True(Field<ActionField>(settings, "UpdateInstall").IsVisible);

        // A document that is not saved yet: the app says so where the update is offered, and fetches nothing.
        vm.Session.Open(TestPdf.WithText("A letter"), null);
        await Settle();
        Press(settings, "UpdateInstall");
        await Settle(() => note.Text.Contains(Strings.English["UpdateSaveFirst"]));
        Assert.Contains(Strings.English["UpdateSaveFirst"], note.Text);
        Assert.StartsWith(string.Format(Strings.English["UpdateAvailable"], "9.4.0", AppInfo.Version), note.Text);

        // Saved, but the app is open in another window as well: that one has to go first.
        var document = In("letter one.pdf");
        File.WriteAllBytes(document, vm.Session.Bytes!);
        vm.Session.MarkSaved(document);
        Press(settings, "UpdateInstall");
        await Settle(() => note.Text.Contains(AppInfo.Name));
        Assert.Contains(string.Format(Strings.English["UpdateCloseOthers"], AppInfo.Name), note.Text);
        Assert.Empty(fetched);
        Assert.Equal(0, closed);
        Capture(window, "43-update-held");

        // Nothing in the way: fetched, checked, handed over to the setup with the document to open again, closed.
        vm.Updater.Running = _ => 1;
        Press(settings, "UpdateInstall");
        await Settle(() => closed > 0);

        Assert.Equal(1, closed);
        Assert.Equal([Downloads + Updater.Checksums, Downloads + "MasterPdfModifier-Setup.exe"], fetched);
        var setup = Assert.Single(started);
        Assert.Equal(In("downloads", "9.4.0", "MasterPdfModifier-Setup.exe"), setup.FileName);
        Assert.EndsWith($"/UPDATE=1 /REOPEN=\"{document}\"", setup.Arguments);
        Assert.Equal(string.Format(Strings.English["UpdateAvailable"], "9.4.0", AppInfo.Version), note.Text);
        Assert.Equal(string.Format(Strings.English["UpdateInstalling"], "9.4.0"), vm.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ACopyThatIsNoneOfTheDownloadsOffersThePageOfTheVersionInstead()
    {
        var (window, vm) = Start();
        vm.Settings.UpdateNotice = true;
        vm.Updates.Redirect = (_, _) => Task.FromResult<string?>(Page);
        vm.Updater.Installed = new Installation(InstallKind.Unknown, In("bin", "MasterPdfModifier.exe"));
        await vm.LookForNewVersion();
        var settings = Tool<SettingsTool>(vm);
        await Settle();

        Assert.Equal(Page, vm.Updates.Page);
        Assert.False(Field<ActionField>(settings, "UpdateInstall").IsVisible);
        Assert.True(Field<ActionField>(settings, "UpdateGet").IsVisible);
        window.Close();
    }

    private static void WriteArchive(string path, params (string Name, string Content)[] files)
    {
        using var file = File.Create(path);
        using var packed = new GZipStream(file, CompressionLevel.Fastest);
        using var archive = new TarWriter(packed, TarEntryFormat.Gnu);
        foreach (var (name, content) in files)
            archive.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
    }
}
