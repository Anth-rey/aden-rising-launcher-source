using System.IO;

namespace AdenRising.Updater.Core;

/// <summary>How a game window is started when the plain way does not get it onto the screen.</summary>
public enum StartMode
{
    /// <summary>As every computer starts it.</summary>
    Normal,
    /// <summary>Held to one processor core until its window is up, then given the rest back.</summary>
    OneCore,
    /// <summary>Without the graphics wrapper (Installer.RefuseTranslation), the way the game ran before it had one.</summary>
    NoTranslation,
    /// <summary>With the engine as it shipped, without our code signature (PlainEngine).</summary>
    PlainEngine,
}

/// <summary>
/// What this computer needed to get the game on the screen, remembered beside the installed client so the next start
/// goes straight there instead of failing first. A computer that starts the plain way never has the file.
/// </summary>
public static class StartHelp
{
    private static string PathFor(string gameDir) => Path.Combine(gameDir, ".updater", "start-mode");

    public static StartMode Saved(string gameDir)
    {
        try
        {
            return File.Exists(PathFor(gameDir)) && Enum.TryParse<StartMode>(File.ReadAllText(PathFor(gameDir)).Trim(), out var mode)
                ? mode : StartMode.Normal;
        }
        catch (IOException) { return StartMode.Normal; }
        catch (UnauthorizedAccessException) { return StartMode.Normal; }
    }

    public static void Save(string gameDir, StartMode mode)
    {
        try
        {
            if (mode == StartMode.Normal) { File.Delete(PathFor(gameDir)); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor(gameDir))!);
            File.WriteAllText(PathFor(gameDir), mode.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The next thing to try after <paramref name="failed"/>, or null when nothing is left.</summary>
    public static StartMode? After(StartMode failed, bool translationInPlace, bool plainEngineInUse) => failed switch
    {
        // The harmless one first: it changes nothing on disk.
        StartMode.Normal => StartMode.OneCore,
        StartMode.OneCore when translationInPlace => StartMode.NoTranslation,
        StartMode.OneCore or StartMode.NoTranslation when !plainEngineInUse => StartMode.PlainEngine,
        _ => null,
    };
}
