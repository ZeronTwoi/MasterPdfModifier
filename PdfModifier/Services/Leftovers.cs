namespace PdfModifier.Services;

/// <summary>
/// Clears away what earlier runs and earlier versions of the app left lying around, so that it does not pile up on
/// the user's disk:
///
/// - the program an update moved aside, and the download that update was made from;
/// - undo histories and dragged-out pages of runs that ended without tidying up (a crash, a power cut);
/// - for the one-file app, the unpacked copies of versions that are no longer the one in use.
///
/// It runs when the app starts, in the background, and only ever touches folders that are the app's own.
/// </summary>
public static class Leftovers
{
    /// <summary>The scratch folder of the app in the temp folder: undo histories, dragged-out pages, installers.</summary>
    public static string Scratch => Path.Combine(Path.GetTempPath(), "PdfModifier");

    /// <summary>Finds what is stale now and removes it without holding up the start.</summary>
    /// <param name="alone">False while another window of the app is open: what could be that one's is then left alone.</param>
    public static void ClearInBackground(Installation installed, bool alone)
    {
        // Found before the app makes anything of its own, so that nothing of this run is ever on the list.
        var stale = Find(installed, AppContext.BaseDirectory, Scratch, Updater.DefaultFolder, alone);
        if (stale.Count > 0) Task.Run(() => Remove(stale, TimeSpan.FromSeconds(1)));
    }

    internal static List<string> Find(Installation installed, string baseDirectory, string scratch, string updates, bool alone)
    {
        var stale = new List<string>();
        if (installed.Kind == InstallKind.OneFile) stale.Add(installed.Program + Updater.SteppedAside);
        if (alone)
        {
            stale.Add(updates);
            if (Directory.Exists(scratch)) stale.AddRange(Directory.EnumerateFileSystemEntries(scratch));
            if (installed.Kind == InstallKind.OneFile) stale.AddRange(OtherUnpackedVersions(baseDirectory));
        }

        return stale.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
    }

    /// <summary>
    /// The folders other versions of the one-file app unpacked themselves into. Each version unpacks into a folder
    /// of its own, next to the others, under ".net" in the temp folder (Windows) or the home folder (Linux); only a
    /// folder that lies exactly there counts.
    /// </summary>
    internal static IEnumerable<string> OtherUnpackedVersions(string baseDirectory)
    {
        var mine = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (Path.GetDirectoryName(mine) is not { } versions || Path.GetFileName(Path.GetDirectoryName(versions)) != ".net") return [];
        return Directory.EnumerateDirectories(versions).Where(folder => !Installation.SameFolder(folder, mine)).ToList();
    }

    /// <summary>
    /// Removes what it can. The program an update moved aside may still be closing, and the setup that started the
    /// app may still be ending, so what does not go at once is tried again for a while.
    /// </summary>
    internal static void Remove(IReadOnlyList<string> stale, TimeSpan pause, int attempts = 15)
    {
        var left = stale.ToList();
        for (var attempt = 0; attempt < attempts && left.Count > 0; attempt++)
        {
            if (attempt > 0) Thread.Sleep(pause);
            left.RemoveAll(TryRemove);
        }
    }

    private static bool TryRemove(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path))
            {
                // A folder something still runs from cannot be renamed on Windows, and is then left whole rather
                // than emptied of the files that happen not to be in use.
                var going = path + ".removing";
                if (OperatingSystem.IsWindows() && !Directory.Exists(going))
                {
                    Directory.Move(path, going);
                    path = going;
                }

                Directory.Delete(path, true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
