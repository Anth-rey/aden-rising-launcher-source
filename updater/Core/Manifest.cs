using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdenRising.Updater.Core;

/// <summary>How the updater is allowed to treat a file.</summary>
public enum FileKind
{
    /// <summary>Must always match the hash: game data, textures, L2.exe, l2.ini.</summary>
    Managed,

    /// <summary>Written once, on a fresh install, then left alone -- resolution,
    /// keybinds, window positions. Re-syncing these would reset the player's
    /// settings on every patch.</summary>
    Seed,
}

public sealed record ManifestEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string KindRaw,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256)
{
    [JsonIgnore]
    public FileKind Kind => KindRaw.Equals("seed", StringComparison.OrdinalIgnoreCase)
        ? FileKind.Seed
        : FileKind.Managed;

    /// <summary>Path as it lands on disk. The manifest is always forward-slashed
    /// so it reads the same whichever machine wrote it.</summary>
    [JsonIgnore]
    public string LocalPath => Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
}

public sealed record Manifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("totalFiles")] int TotalFiles,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("files")] IReadOnlyList<ManifestEntry> Files)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Names a manifest may never write, whatever it says. The launcher keeps a
    /// copy of itself at the root of the game folder and Windows looks there
    /// first for any DLL the launcher asks for, so a root-level .exe or .dll is
    /// a way to run code as the launcher rather than as the game. The
    /// .updater folder is the launcher's own bookkeeping.
    /// </summary>
    private static bool Forbidden(string path)
    {
        var segments = path.Split('/');
        var name = segments[^1];
        if (segments.Length == 1
            && (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Aden Rising.exe", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (segments[0].Equals(".updater", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." ) return true;
            // "con", "nul", "com1" and friends open devices, not files.
            var stem = segment.Split('.')[0];
            if (stem.Length is 3 or 4 && DeviceNames.Contains(stem.ToUpperInvariant())) return true;
        }
        return false;
    }

    private static readonly HashSet<string> DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Opens a signed manifest envelope (tools/upload-to-r2.mjs) and parses what is inside.</summary>
    public static (Manifest Manifest, string IssuedAt) ParseSigned(string envelopeJson)
    {
        var signed = Signing.Open(envelopeJson, "manifest");
        return (Parse(signed.Body.GetRawText()), signed.IssuedAt);
    }

    public static Manifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(json, Options)
            ?? throw new InvalidDataException("the manifest is empty");

        if (manifest.Files is null || manifest.Files.Count == 0)
            throw new InvalidDataException("the manifest lists no files");

        // A manifest that disagrees with itself is not one we should act on:
        // it would silently under-download and leave a broken client.
        if (manifest.Files.Count != manifest.TotalFiles)
        {
            throw new InvalidDataException(
                $"the manifest says {manifest.TotalFiles} files but lists {manifest.Files.Count}");
        }

        foreach (var f in manifest.Files)
        {
            if (f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"{f.Path}: \"{f.Sha256}\" is not a sha-256");
            // ".." climbs out; a rooted path (C:\, \\server, \foo) is outside by
            // definition; ':' is a drive letter or an NTFS alternate stream;
            // '\' would let the same path be written two ways.
            if (f.Path.Length == 0 || f.Path.Length > 400
                || f.Path.Contains("..") || f.Path.Contains(':') || f.Path.Contains('\\')
                || System.IO.Path.IsPathRooted(f.Path) || Forbidden(f.Path))
                throw new InvalidDataException($"{f.Path}: refuses to write there");
            if (f.Size < 0)
                throw new InvalidDataException($"{f.Path}: negative size");
        }

        return manifest;
    }
}
