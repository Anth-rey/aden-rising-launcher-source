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
            if (f.Path.Contains("..") || System.IO.Path.IsPathRooted(f.Path))
                throw new InvalidDataException($"{f.Path}: refuses to write outside the game folder");
        }

        return manifest;
    }
}
