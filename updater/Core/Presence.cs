namespace AdenRising.Updater.Core;

/// <summary>
/// The launcher's one-shot mark for a game window it is about to start:
/// <c>.updater\launch.token</c> in the game folder, written right before
/// L2.exe is started and removed a few seconds later. The client's L2.exe
/// looks for it at startup and, when it is missing, shows a dialog, starts
/// the launcher and exits -- so a double-clicked L2.exe, launcher running or
/// not, ends up in the launcher instead of at a refusal in game.
///
/// A file rather than a kernel object on purpose: it works whatever rights
/// the game runs with. Left behind by a crash between write and delete, it
/// admits one hand-started window until the next Play removes it; the server
/// still refuses a window the launcher did not report, so nothing is lost
/// but a bit of guidance.
/// </summary>
public static class Presence
{
    private const string FileName = "launch.token";
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);

    private static string PathIn(string gameDir) => Path.Combine(gameDir, ".updater", FileName);

    /// <summary>Marks the next game start as the launcher's; the mark goes away by itself.</summary>
    public static void Grant(string gameDir)
    {
        var path = PathIn(gameDir);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("u") + Environment.NewLine);
        }
        catch { /* a folder we cannot write is a folder the install already failed in */ }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Lifetime);
            Revoke(gameDir);
        });
    }

    /// <summary>Removes the mark, whether or not it was used.</summary>
    public static void Revoke(string gameDir)
    {
        try
        {
            var path = PathIn(gameDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
