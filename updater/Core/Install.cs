using System.Diagnostics;

namespace AdenRising.Updater.Core;

/// <summary>
/// Making the launcher survive its own download folder.
///
/// A player fetches the .exe with a browser, runs it, installs the game, and
/// then tidies up their downloads -- which is the reasonable thing to do with
/// a file called an installer. Until now that threw the launcher away: the
/// desktop shortcut pointed at wherever the .exe happened to be sitting when
/// it made the shortcut, so clearing Downloads left a shortcut to nothing and
/// no way back in short of downloading it again and meeting the unrecognised
/// app warning a second time.
///
/// So the launcher puts a copy of itself in the game folder and points every
/// shortcut there. The game folder is the one place that is certain to still
/// exist as long as the player still plays, and it is the folder they chose.
/// </summary>
public static class Install
{
    /// <summary>The name the copy takes in the game folder. Not the name of the
    /// downloaded file, which browsers decorate with (1) and similar.</summary>
    public const string ExeName = "Aden Rising.exe";

    public static string InstalledExe(string gameDir) => Path.Combine(gameDir, ExeName);

    /// <summary>
    /// Puts this launcher in the game folder and hands back the path a shortcut
    /// should point at.
    ///
    /// Returns the copy when there is one to point at, and the running .exe
    /// when there is not -- a shortcut to the file they downloaded is worse
    /// than the copy and better than nothing.
    /// </summary>
    public static string PlaceInGameFolder(string gameDir)
    {
        var running = SelfUpdate.ExePath;
        var target = InstalledExe(gameDir);

        // Already the copy: this is the normal case after the first run, and
        // File.Copy onto itself would throw rather than do nothing.
        if (string.Equals(Path.GetFullPath(running), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return target;

        try
        {
            if (!Directory.Exists(gameDir)) return running;

            // Older than what is already installed means this is a stale copy
            // being run out of a downloads folder -- someone kept the original
            // file and clicked it again months later. Letting it overwrite the
            // copy would undo every self-update since, and the shortcuts point
            // at that copy, so the player would silently go backwards.
            if (File.Exists(target) && VersionOf(target) is { } there && there >= SelfUpdate.Current)
                return target;

            File.Copy(running, target, overwrite: true);
            return target;
        }
        catch
        {
            // A game folder we cannot write to is not worth stopping an install
            // over; the launcher still works from where it is.
            return running;
        }
    }

    /// <summary>
    /// Desktop and Start menu.
    ///
    /// <paramref name="force"/> is the player having asked for shortcuts, which
    /// they are asked once, on the first run. Without it this only corrects
    /// shortcuts that already exist, and never adds one back: someone who
    /// deleted theirs on purpose should not have to delete it again after
    /// every patch.
    /// </summary>
    public static void CreateShortcuts(string target, string gameDir, bool force)
    {
        // The desktop is the player's own space. A shortcut that is not there
        // is a shortcut they threw away, and putting it back after every patch
        // would be rude, so it is only ever corrected -- never re-added.
        Ensure(Environment.SpecialFolder.DesktopDirectory, addIfMissing: force);

        // The Start menu is not decoration, it is where an installed program is
        // expected to be found, and nobody curates it the way they curate a
        // desktop. So it is kept present: absent means it gets made, whatever
        // the reason it is absent.
        //
        // This is also the belt to the earlier braces. On the 1.0.12 install
        // the desktop shortcut appeared and this one did not, and the cause was
        // never found -- the same code makes both, and run on its own it makes
        // both. Whatever that was, it was a one-off that the previous rule
        // would have made permanent: a missing entry was never looked at again.
        Ensure(Environment.SpecialFolder.Programs, addIfMissing: true);

        void Ensure(Environment.SpecialFolder folder, bool addIfMissing)
        {
            try
            {
                var dir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(dir)) return;
                Write(Path.Combine(dir, "Aden Rising.lnk"), target, gameDir, addIfMissing);
            }
            catch { /* a missing shortcut is not worth failing an install over */ }
        }
    }

    private static void Write(string link, string target, string gameDir, bool addIfMissing)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell is null) return;

        var write = File.Exists(link) ? PointsElsewhere(shell, link, target) : addIfMissing;
        if (!write) return;

        dynamic sc = shell.CreateShortcut(link);
        sc.TargetPath = target;
        // The folder is passed on so a shortcut still means the same install
        // even for a player who keeps two of them.
        sc.Arguments = $"\"{gameDir}\"";
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.Description = "Aden Rising";
        sc.Save();
    }

    /// <summary>
    /// Whether an existing shortcut points somewhere other than the copy in the
    /// game folder.
    ///
    /// Waiting for it to be actually broken would be too late for everyone who
    /// installed before this existed: their shortcut points into Downloads at a
    /// file that is still there, so nothing looks wrong, and it breaks on the
    /// day they tidy up -- by which time the launcher is not running to fix it.
    /// Pointing it at the copy now is what makes that day uneventful.
    /// </summary>
    private static bool PointsElsewhere(dynamic shell, string link, string target)
    {
        try
        {
            dynamic sc = shell.CreateShortcut(link);
            string existing = sc.TargetPath;
            if (string.IsNullOrEmpty(existing)) return true;
            return !string.Equals(Path.GetFullPath(existing), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static Version? VersionOf(string exe)
    {
        try
        {
            var raw = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            return Version.TryParse(raw, out var v) ? v : null;
        }
        catch { return null; }
    }
}
