using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace AdenRising.Updater.Core;

/// <summary>
/// Fetches content-addressed blobs and keeps them in a local cache. Everything
/// here assumes the connection will fail: a half-downloaded 197 MB file is
/// resumed rather than restarted, and nothing is trusted until its hash
/// matches what the manifest asked for.
/// </summary>
public sealed class BlobStore(HttpClient http, Uri baseUri, string cacheDir)
{
    private const int MaxAttempts = 5;

    /// <summary>Big enough that the loop is not the thing limiting throughput,
    /// small enough that a few of these in flight cost nothing worth counting.</summary>
    private const int BufferSize = 1 << 20;

    public string PathFor(string sha256) => Path.Combine(cacheDir, sha256);

    /// <summary>
    /// Makes sure the blob is in the cache and correct, and returns its path.
    /// <paramref name="onBytes"/> is called with each chunk actually received,
    /// so a resumed download does not re-report progress it already made.
    /// </summary>
    public async Task<string> EnsureAsync(
        string sha256, long size, Action<long>? onBytes = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var final = PathFor(sha256);

        if (File.Exists(final) && new FileInfo(final).Length == size)
            return final;

        var partial = final + ".part";
        Exception? last = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await FetchAsync(sha256, size, partial, onBytes, ct);

                var actual = await HashFileAsync(partial, ct);
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Corrupt, truncated, or something in the middle rewrote it.
                    // Start clean rather than resuming onto bad bytes -- and
                    // give back the progress already reported for them, or the
                    // next attempt counts the same bytes twice and the bar
                    // climbs past 100% with a negative time remaining.
                    var discarded = new FileInfo(partial).Length;
                    File.Delete(partial);
                    onBytes?.Invoke(-discarded);
                    throw new InvalidDataException(
                        $"downloaded content does not match its hash (wanted {sha256[..12]}, got {actual[..12]})");
                }

                File.Move(partial, final, overwrite: true);
                return final;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == MaxAttempts) break;
                // Back off, but not so far that a long download stalls on a blip.
                var delay = TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));
                await Task.Delay(delay, ct);
            }
        }

        throw new IOException($"could not fetch {sha256[..12]} after {MaxAttempts} attempts", last);
    }

    private async Task FetchAsync(
        string sha256, long size, string partial, Action<long>? onBytes, CancellationToken ct)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > size) { File.Delete(partial); have = 0; }
        if (have == size) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"files/{sha256}"));
        if (have > 0) request.Headers.Range = new(have, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Asked to resume and got the whole file back: the server ignored the
        // range, so start from zero rather than appending to what we had.
        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            File.Delete(partial);
            have = 0;
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);

        // Deliberately not FileMode.Append. Combined with async I/O, Windows
        // re-establishes the end of the file on every write, and the measured
        // cost was roughly twentyfold -- 2 MB/s here where the same connection
        // fetched at 50 MB/s outside the updater. Open once, seek once, stream.
        await using var target = new FileStream(
            partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (have > 0) target.Seek(have, SeekOrigin.Begin);
        else target.SetLength(0);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            have += read;
            // The manifest said how big the file is. A stream that keeps going
            // past that is not the file, and it should not be allowed to fill
            // the disk while it says so.
            if (have > size) throw new InvalidDataException($"{sha256[..12]}: more bytes than the manifest says");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            onBytes?.Invoke(read);
        }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}
