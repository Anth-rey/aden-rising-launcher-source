using System.Diagnostics;
using System.Security.Cryptography;

namespace AdenRising.Updater.Core;

/// <summary>
/// The few things the launcher needs that newer .NET has and the .NET Framework 4.8 of the Windows 7/8 build does not.
/// One spelling for both builds, so the code that uses them stays the same everywhere.
/// </summary>
internal static class Compat
{
    public static void Move(string from, string to, bool overwrite)
    {
#if NETFRAMEWORK
        if (overwrite && File.Exists(to))
        {
            File.Replace(from, to, null);
            return;
        }
        File.Move(from, to);
#else
        File.Move(from, to, overwrite);
#endif
    }

    public static byte[] Sha256(byte[] data)
    {
#if NETFRAMEWORK
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
#else
        return SHA256.HashData(data);
#endif
    }

    public static async Task<byte[]> Sha256Async(Stream stream, CancellationToken ct)
    {
#if NETFRAMEWORK
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(buffer, 0, 0);
        return sha.Hash!;
#else
        return await SHA256.HashDataAsync(stream, ct);
#endif
    }

    public static string Hex(byte[] data) => Hex(data, 0, data.Length);

    public static string Hex(byte[] data, int offset, int length)
    {
#if NETFRAMEWORK
        var sb = new System.Text.StringBuilder(length * 2);
        for (var i = offset; i < offset + length; i++) sb.Append(data[i].ToString("X2"));
        return sb.ToString();
#else
        return Convert.ToHexString(data, offset, length);
#endif
    }

    public static string HexLower(byte[] data) => Hex(data).ToLowerInvariant();

    public static string? ProcessPath
    {
        get
        {
#if NETFRAMEWORK
            using var me = Process.GetCurrentProcess();
            return me.MainModule?.FileName;
#else
            return Environment.ProcessPath;
#endif
        }
    }

    public static int ProcessId
    {
        get
        {
#if NETFRAMEWORK
            using var me = Process.GetCurrentProcess();
            return me.Id;
#else
            return Environment.ProcessId;
#endif
        }
    }

    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Time since a <see cref="Stopwatch.GetTimestamp"/> reading.</summary>
    public static TimeSpan Elapsed(long startTimestamp) =>
        TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency);

    public static Task WriteAllTextAsync(string path, string text, CancellationToken ct = default)
    {
#if NETFRAMEWORK
        File.WriteAllText(path, text);
        return Task.CompletedTask;
#else
        return File.WriteAllTextAsync(path, text, ct);
#endif
    }
}
