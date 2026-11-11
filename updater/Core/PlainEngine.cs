using System.Security.Cryptography;

namespace AdenRising.Updater.Core;

/// <summary>
/// The engine and its DirectX helper exactly as they came, without our code signature, for the one kind of computer
/// that needs them.
///
/// Every file of the client is signed (since client 1.0.130) because Smart App Control in Windows 11 will not load an
/// unsigned one. The engine (a 2007 build wrapped in a protector of its day) and dsetup.dll (Microsoft's, loaded by
/// the engine at start-up) were never ours to begin with, and the signature is the only thing we changed in them. On
/// a computer where the game sits at start-up with no window (2026-11-10: Windows 10, two players) that is the one
/// difference between our client and the Interlude clients that do run there, so it is the last thing the launcher
/// tries.
///
/// Nothing is downloaded: the signature is cut back off the installed file, and the result has to come out byte for
/// byte as the original, or it is not used. Only those exact files are ever accepted in place of the signed ones
/// (<see cref="Integrity"/>, <see cref="Installer"/>), so the flag below cannot be used to slip in anything else.
/// </summary>
public static class PlainEngine
{
    private sealed record Original(string ManifestPath, string Sha256, int Length, uint Checksum);

    private static readonly Original[] Files =
    [
        new("system/engine.dll", "6b445f4c7da9cb56b079e8a23d699bacfdef6c9f24887d8c633772f207d1181d", 30445588, 0x01d0f58f),
        new("system/dsetup.dll", "d48dfa0f6177fe307c97f36c0af6d2d9027b42c263ddd31b6d463141b0e7d824", 62996, 0x00017e8b),
    ];

    private static string FlagPath(string gameDir) => System.IO.Path.Combine(gameDir, ".updater", "plain-engine");
    private static string SignedCopyPath(string gameDir, Original f) =>
        System.IO.Path.Combine(gameDir, ".updater", System.IO.Path.GetFileName(f.ManifestPath) + ".signed");
    private static string FullPath(string gameDir, Original f) =>
        System.IO.Path.Combine(gameDir, f.ManifestPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Whether this computer runs the unsigned files.</summary>
    public static bool InUse(string gameDir) => File.Exists(FlagPath(gameDir));

    /// <summary>Whether <paramref name="manifestPath"/> is one of the files this swaps.</summary>
    public static bool Covers(string manifestPath) =>
        Files.Any(f => f.ManifestPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts the unsigned files in place of the signed ones, which are kept aside so <see cref="Drop"/> can put them
    /// back. True when the unsigned engine is in place (already, or now); false when the installed engine is not one
    /// it can be made from. dsetup.dll goes along when it can and is not a reason to give up when it cannot.
    /// </summary>
    public static bool Use(string gameDir)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FlagPath(gameDir))!);
            if (!Swap(gameDir, Files[0])) return false;
            foreach (var f in Files.Skip(1)) Swap(gameDir, f);
            WriteFlag(gameDir);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Swap(string gameDir, Original f)
    {
        try
        {
            var path = FullPath(gameDir, f);
            var current = File.ReadAllBytes(path);
            if (Convert.ToHexString(SHA256.HashData(current)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) return true;

            var plain = Strip(current, f);
            if (plain is null) return false;

            var temp = path + ".plain";
            File.WriteAllBytes(temp, plain);
            File.Copy(path, SignedCopyPath(gameDir, f), overwrite: true);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void WriteFlag(string gameDir) =>
        File.WriteAllText(FlagPath(gameDir), $"{DateTime.Now:u}{Environment.NewLine}The signed engine never opened a window here.{Environment.NewLine}");

    /// <summary>Takes the unsigned files back (they were tried and did not help) and puts the signed ones in their place.</summary>
    public static void Drop(string gameDir)
    {
        foreach (var f in Files)
        {
            try
            {
                if (File.Exists(SignedCopyPath(gameDir, f))) File.Move(SignedCopyPath(gameDir, f), FullPath(gameDir, f), overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { File.Delete(FlagPath(gameDir)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Whether <paramref name="sha256"/> is the unsigned original of <paramref name="manifestPath"/> and this computer was given it.</summary>
    public static bool Accepts(string gameDir, string manifestPath, string sha256) =>
        InUse(gameDir) && Files.Any(f => f.ManifestPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase)
                                         && f.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>The signed file with its signature taken off, or null when that does not give the original.</summary>
    private static byte[]? Strip(byte[] signed, Original f)
    {
        if (signed.Length < f.Length || signed.Length < 0x40) return null;
        var pe = BitConverter.ToInt32(signed, 0x3c);
        if (pe <= 0 || pe + 24 + 96 + 8 * 5 > signed.Length) return null;
        if (BitConverter.ToUInt16(signed, pe + 24) != 0x10b) return null;   // both are 32-bit files
        var checksum = pe + 24 + 64;
        var security = pe + 24 + 96 + 4 * 8;

        var plain = signed.AsSpan(0, f.Length).ToArray();
        BitConverter.TryWriteBytes(plain.AsSpan(checksum, 4), f.Checksum);
        plain.AsSpan(security, 8).Clear();

        var hash = Convert.ToHexString(SHA256.HashData(plain));
        return hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase) ? plain : null;
    }
}
