namespace AdenRising.Updater.Core;

/// <summary>
/// The quick check before every Play: are the files that make the client
/// ours still the ones we installed?
///
/// The server admits only windows this launcher started, so the one way left
/// to play with a foreign client is to copy its files into our folder and
/// press Play. This closes that: the handful of files that decide what the
/// client is are hashed against the record of the last install, and a
/// mismatch stops the launch with a pointer to Verify files. Plain reading
/// and hashing of our own files, a second or two at most, nothing else.
///
/// Only managed files are listed. A seed such as NWindow.dll belongs to the
/// player once written and a GM keeps a patched one on purpose.
/// </summary>
public static class Integrity
{
    private static readonly string[] Core =
    [
        "system/L2.exe",
        "system/engine.dll",
        "system/Core.dll",
        "system/l2.ini",
        "system/Interface.u",
        // The graphics wrapper is the first native DLL the client loads, from
        // its own folder -- a swapped one runs before the game does.
        "system/d3d9.dll",
        "system/dgVoodoo.conf",
    ];

    private static readonly string[] Translation = ["system/d3d9.dll", "system/dgVoodoo.conf"];

    /// <returns>The first core file that is missing or not ours, or null when all match.</returns>
    public static async Task<string?> FirstMismatchAsync(string gameDir, Installer installer)
    {
        foreach (var path in Core)
        {
            // Deliberately removed on this machine (Installer.RefuseTranslation): their absence is the point.
            if (installer.TranslationRefused && Translation.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;

            var expected = installer.RecordedHash(path);
            if (expected is null) continue;   // not part of this install's record: nothing to compare

            var full = Path.Combine(gameDir, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return path;

            var actual = await BlobStore.HashFileAsync(full);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }
}
