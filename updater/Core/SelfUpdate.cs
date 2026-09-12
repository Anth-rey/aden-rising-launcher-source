using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdenRising.Updater.Core;

/// <summary>What the site says the current launcher is.</summary>
public sealed record LauncherRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("path")] string Path);

/// <summary>
/// The launcher replacing itself.
///
/// This exists for one reason: a player downloads the .exe from a browser,
/// which marks it as coming from the internet, and Windows then warns about an
/// unrecognised app. Nothing we can buy removes that warning today -- signing
/// only lets reputation build over time, and even EV certificates stopped
/// granting it outright in 2024. What we can do is make the download happen
/// once. Every later version arrives through here, carries no download mark,
/// and is never questioned.
///
/// Without this, every fix to the launcher means asking the whole community to
/// go and fetch a new file, and each of them meets the warning again.
/// </summary>
public static class SelfUpdate
{
    /// <summary>Where this launcher is running from. `Assembly.Location` is
    /// empty in a single-file build, so it cannot be used here.</summary>
    public static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("cannot tell where this launcher is running from");

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    private static string OldPath => ExePath + ".old";
    private static string NewPath => ExePath + ".new";

    /// <summary>
    /// Clears the copy left behind by the previous update. It cannot be deleted
    /// at the time because it is the running program; by the next start it is
    /// only a file.
    /// </summary>
    public static void ForgetPrevious()
    {
        foreach (var stale in new[] { OldPath, NewPath })
        {
            try { if (File.Exists(stale)) File.Delete(stale); }
            catch { /* it will still be there next time, and it harms nothing */ }
        }
    }

    /// <summary>
    /// The release on offer, or null when we are already current or could not
    /// ask. Never throws: a launcher that refuses to start because it could
    /// not check for its own update would be worse than an old launcher.
    /// </summary>
    public static async Task<LauncherRelease?> CheckAsync(
        HttpClient http, Uri baseUrl, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetStringAsync(new Uri(baseUrl, "launcher/latest.json"), ct);
            var release = JsonSerializer.Deserialize<LauncherRelease>(json);

            if (release is null
                || !Version.TryParse(release.Version, out var offered)
                || release.Sha256.Length != 64
                || release.Size <= 0)
                return null;

            return offered > Current ? release : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches the new launcher, puts it where this one is, and starts it.
    /// Returns false when the swap could not even be attempted -- most often
    /// because the launcher lives somewhere the player cannot write, such as
    /// Program Files. That is not worth stopping for: the old launcher still
    /// works, so it simply carries on.
    /// </summary>
    public static async Task<bool> ApplyAsync(
        HttpClient http, Uri baseUrl, LauncherRelease release,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        ForgetPrevious();

        try
        {
            await DownloadAsync(http, new Uri(baseUrl, release.Path), NewPath, release.Size, onProgress, ct);
        }
        catch
        {
            Discard(NewPath);
            return false;
        }

        var actual = await BlobStore.HashFileAsync(NewPath, ct);
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Whatever that file is, it is not the launcher we were promised,
            // and it is the one thing here that must never be run on trust.
            Discard(NewPath);
            return false;
        }

        // A running .exe cannot be overwritten, but it can be renamed out of
        // the way -- Windows keeps serving the open image from the moved file.
        try
        {
            File.Move(ExePath, OldPath);
        }
        catch
        {
            Discard(NewPath);
            return false;
        }

        try
        {
            File.Move(NewPath, ExePath);
        }
        catch
        {
            // Put the player's launcher back before anything else. Leaving them
            // with no .exe at all is the only genuinely unrecoverable outcome
            // in this whole file.
            try { File.Move(OldPath, ExePath); } catch { }
            Discard(NewPath);
            return false;
        }

        Relaunch();
        return true;
    }

    /// <summary>Starts the replacement, passing on whatever this one was given
    /// so a shortcut with a folder in it still means the same thing.</summary>
    private static void Relaunch()
    {
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = true };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            start.ArgumentList.Add(arg);

        Process.Start(start);
    }

    private static void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task DownloadAsync(
        HttpClient http, Uri from, string to, long expected,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        using var response = await http.GetAsync(from, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);

        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            onProgress?.Invoke(done, expected);
        }
    }
}
