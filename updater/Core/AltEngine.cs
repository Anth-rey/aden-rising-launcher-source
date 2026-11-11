namespace AdenRising.Updater.Core;

/// <summary>
/// The engine for computers where ours never opens a window.
///
/// Our engine.dll is NCsoft's 2007 build inside the protector NCsoft wrapped it in, and on some Windows 10 computers
/// that protector never gets past its own start-up (2026-11-10/11: two players, every step of the launcher's self-help
/// stuck, all other Interlude clients working for them). The Interlude clients that do run there use NCsoft's engine
/// without that wrapper -- this is one, a build from 2007-09-03. Every other computer keeps ours; only one that has
/// proved it cannot start the game gets this, and only this exact file (hash below) is ever accepted in place of ours
/// (<see cref="Integrity"/>, <see cref="Installer"/>). It comes from our own file host like any other client file.
/// </summary>
public static class AltEngine
{
    public const string ManifestPath = "system/engine.dll";
    public const string Sha256 = "508974c711f207402719e92737e211a2f029c95c2f68fc0e1c31fcbb9dbb232d";
    public const long Size = 30704468;

    private static string FlagPath(string gameDir) => Path.Combine(gameDir, ".updater", "alt-engine");
    private static string OursPath(string gameDir) => Path.Combine(gameDir, ".updater", "engine.ours");
    private static string EnginePath(string gameDir) => Path.Combine(gameDir, "system", "engine.dll");

    /// <summary>Whether this computer runs the alternative engine.</summary>
    public static bool InUse(string gameDir) => File.Exists(FlagPath(gameDir));

    /// <summary>Whether <paramref name="sha256"/> at <paramref name="manifestPath"/> is the alternative engine on a computer that was given it.</summary>
    public static bool Accepts(string gameDir, string manifestPath, string sha256) =>
        InUse(gameDir) && manifestPath.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase)
                       && sha256.Equals(Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the alternative engine in place, keeping ours aside for <see cref="Drop"/>. True when it is in place
    /// (already, or now); false when it could not be fetched or written.
    /// </summary>
    public static async Task<bool> UseAsync(string gameDir, BlobStore blobs)
    {
        try
        {
            var path = EnginePath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(FlagPath(gameDir))!);
            if (File.Exists(path) && (await BlobStore.HashFileAsync(path)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            {
                WriteFlag(gameDir);
                return true;
            }

            var blob = await blobs.EnsureAsync(Sha256, Size);
            if (File.Exists(path)) File.Copy(path, OursPath(gameDir), overwrite: true);
            // Fails while another game window still has the engine open: then nothing is marked and ours stays.
            File.Copy(blob, path, overwrite: true);
            WriteFlag(gameDir);
            return true;
        }
        catch { return false; }
    }

    private static void WriteFlag(string gameDir) =>
        File.WriteAllText(FlagPath(gameDir), $"{DateTime.Now:u}{Environment.NewLine}Our engine never opened a window here.{Environment.NewLine}");

    /// <summary>Takes the alternative engine back (it was tried and did not help) and puts ours in its place.</summary>
    public static void Drop(string gameDir)
    {
        try
        {
            if (File.Exists(OursPath(gameDir))) Compat.Move(OursPath(gameDir), EnginePath(gameDir), overwrite: true);
            File.Delete(FlagPath(gameDir));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
