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
    [property: JsonPropertyName("path")] string Path)
{
    /// <summary>When the release note was signed; what TrustLog remembers.</summary>
    [JsonIgnore]
    public string IssuedAt { get; init; } = "";
}

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
///
/// Which also makes this the most dangerous code in the launcher: whatever it
/// installs, runs. So the release note has to arrive signed by the release key
/// (Signing), the file it names has to live on our own host over https, and
/// the bytes have to match the note. Any doubt, and the launcher keeps the
/// version it has.
/// </summary>
public static class SelfUpdate
{
    /// <summary>Where this launcher is running from. `Assembly.Location` is
    /// empty in a single-file build, so it cannot be used here.</summary>
    public static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("cannot tell where this launcher is running from");

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>The signed release note; the unsigned launcher/latest.json is history.</summary>
    private const string ReleaseNote = "launcher/latest.v2.json";

    /// <summary>A launcher is 60-80 MB; anything past this is not one of ours.</summary>
    private const long MaxLauncherBytes = 256L * 1024 * 1024;

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
            var json = await http.GetStringAsync(new Uri(baseUrl, ReleaseNote), ct);
            var signed = Signing.Open(json, "launcher");
            TrustLog.RequireNotOlder("launcher", signed.IssuedAt);

            var release = signed.Body.Deserialize<LauncherRelease>();
            if (release is null
                || !Version.TryParse(release.Version, out var offered)
                || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit)
                || release.Size <= 0 || release.Size > MaxLauncherBytes
                || !IsOurs(baseUrl, release.Path))
                return null;

            return offered > Current ? release with { IssuedAt = signed.IssuedAt } : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The file a release note names has to sit on the same host the note
    /// came from, reached over https, under a plain relative path. A note
    /// that pointed anywhere else would be one somebody else wrote.
    /// </summary>
    private static bool IsOurs(Uri baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 200) return false;
        if (path.Contains("..") || path.Contains(':') || path.Contains('\\')) return false;
        if (path.StartsWith('/') || path.StartsWith("//")) return false;
        if (!Uri.TryCreate(baseUrl, path, out var resolved)) return false;
        return resolved.Scheme == Uri.UriSchemeHttps
            && resolved.Host.Equals(baseUrl.Host, StringComparison.OrdinalIgnoreCase)
            && baseUrl.IsBaseOf(resolved);
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
        if (!IsOurs(baseUrl, release.Path)) return false;

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
        if (new FileInfo(NewPath).Length != release.Size
            || !actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
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

        TrustLog.Accept("launcher", release.IssuedAt);
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
            done += read;
            // The note said how big it is; a stream that keeps going is not it.
            if (done > expected) throw new InvalidDataException("the download is larger than the release note says");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            onProgress?.Invoke(done, expected);
        }
    }
}
