using System.Security.Cryptography;

namespace AdenRising.Updater.Core;

/// <summary>
/// The engine exactly as it came, without our code signature, for the one kind of computer that needs it.
///
/// Every file of the client is signed (since client 1.0.130) because Smart App Control in Windows 11 will not load an
/// unsigned one. The engine is the one file that was never ours to begin with: a 2007 build wrapped in a protector of
/// its day, and the signature is the only thing we changed in it. On a computer where that engine sits at start-up
/// with no window (2026-11-10: a hand-stripped Windows 10, both of one player's computers) it is the one difference
/// between our client and the Interlude clients that do run there, so it is the last thing the launcher tries.
///
/// Nothing is downloaded: the signature is cut back off the installed file, and the result has to come out byte for
/// byte as the original, or it is not used. Only that exact file is ever accepted in place of the signed one
/// (<see cref="Integrity"/>), so the flag below cannot be used to slip in any other engine.
/// </summary>
public static class PlainEngine
{
    public const string ManifestPath = "system/engine.dll";

    /// <summary>The engine as it shipped, before signing: its hash, length and header checksum.</summary>
    public const string OriginalSha256 = "6b445f4c7da9cb56b079e8a23d699bacfdef6c9f24887d8c633772f207d1181d";
    private const int OriginalLength = 30445588;
    private const uint OriginalChecksum = 0x01d0f58f;

    private static string FlagPath(string gameDir) => System.IO.Path.Combine(gameDir, ".updater", "plain-engine");
    private static string SignedCopyPath(string gameDir) => System.IO.Path.Combine(gameDir, ".updater", "engine.signed");
    private static string EnginePath(string gameDir) => System.IO.Path.Combine(gameDir, "system", "engine.dll");

    /// <summary>Whether this computer runs the unsigned engine.</summary>
    public static bool InUse(string gameDir) => File.Exists(FlagPath(gameDir));

    /// <summary>
    /// Puts the unsigned engine in place of the signed one, which is kept aside so <see cref="Drop"/> can put it back.
    /// True when the unsigned one is in place (already, or now); false when the installed file is not one it can be
    /// made from.
    /// </summary>
    public static bool Use(string gameDir)
    {
        try
        {
            var path = EnginePath(gameDir);
            var current = File.ReadAllBytes(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FlagPath(gameDir))!);
            if (Convert.ToHexString(SHA256.HashData(current)).Equals(OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                WriteFlag(gameDir);
                return true;
            }

            var plain = Strip(current);
            if (plain is null) return false;

            var temp = path + ".plain";
            File.WriteAllBytes(temp, plain);
            File.Copy(path, SignedCopyPath(gameDir), overwrite: true);
            WriteFlag(gameDir);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void WriteFlag(string gameDir) =>
        File.WriteAllText(FlagPath(gameDir), $"{DateTime.Now:u}{Environment.NewLine}The signed engine never opened a window here.{Environment.NewLine}");

    /// <summary>Takes the unsigned engine back (it was tried and did not help) and puts the signed one in its place.</summary>
    public static void Drop(string gameDir)
    {
        try
        {
            if (File.Exists(SignedCopyPath(gameDir))) File.Move(SignedCopyPath(gameDir), EnginePath(gameDir), overwrite: true);
            File.Delete(FlagPath(gameDir));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Whether <paramref name="sha256"/> is the unsigned engine and this computer was given it.</summary>
    public static bool Accepts(string gameDir, string sha256) =>
        InUse(gameDir) && sha256.Equals(OriginalSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>The signed file with its signature taken off, or null when that does not give the original.</summary>
    private static byte[]? Strip(byte[] signed)
    {
        if (signed.Length < OriginalLength || signed.Length < 0x40) return null;
        var pe = BitConverter.ToInt32(signed, 0x3c);
        if (pe <= 0 || pe + 24 + 96 + 8 * 5 > signed.Length) return null;
        var magic = BitConverter.ToUInt16(signed, pe + 24);
        if (magic != 0x10b) return null;   // the engine is a 32-bit file
        var checksum = pe + 24 + 64;
        var security = pe + 24 + 96 + 4 * 8;

        var plain = signed.AsSpan(0, OriginalLength).ToArray();
        BitConverter.TryWriteBytes(plain.AsSpan(checksum, 4), OriginalChecksum);
        plain.AsSpan(security, 8).Clear();

        var hash = Convert.ToHexString(SHA256.HashData(plain));
        return hash.Equals(OriginalSha256, StringComparison.OrdinalIgnoreCase) ? plain : null;
    }
}
