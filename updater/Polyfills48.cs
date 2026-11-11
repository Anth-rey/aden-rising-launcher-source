#if NETFRAMEWORK
// Language features and small library members the Windows 7/8 build (.NET Framework 4.8) lacks. Nothing here exists
// in the main build: it compiles only for net48.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace System.Threading
{
    /// <summary>The C# 13 lock type, done with Monitor: <c>lock (x)</c> on it compiles to EnterScope/Dispose.</summary>
    internal sealed class Lock
    {
        public Scope EnterScope()
        {
            Monitor.Enter(this);
            return new Scope(this);
        }

        public readonly ref struct Scope
        {
            private readonly Lock _owner;
            internal Scope(Lock owner) => _owner = owner;
            public void Dispose() => Monitor.Exit(_owner);
        }
    }
}

namespace System
{
    internal readonly struct Index
    {
        private readonly int _value;
        public Index(int value, bool fromEnd = false) => _value = fromEnd ? ~value : value;
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public static Index Start => new(0);
        public static Index End => new(0, true);
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int value) => new(value);
    }

    internal readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);
    }
}

namespace AdenRising.Updater
{
    internal static class Net48Extensions
    {
        public static Task<string> GetStringAsync(this HttpClient http, string url, CancellationToken ct) =>
            GetString(http, url, ct);

        public static Task<string> GetStringAsync(this HttpClient http, Uri url, CancellationToken ct) =>
            GetString(http, url.ToString(), ct);

        private static async Task<string> GetString(HttpClient http, string url, CancellationToken ct)
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken ct) => content.ReadAsStreamAsync();

        public static Task<int> ReadAsync(this Stream stream, byte[] buffer, CancellationToken ct) =>
            stream.ReadAsync(buffer, 0, buffer.Length, ct);

        public static Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (MemoryMarshal.TryGetArray(data, out var segment))
                return stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, ct);
            var copy = data.ToArray();
            return stream.WriteAsync(copy, 0, copy.Length, ct);
        }

        public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;

        public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;

        public static Task WaitForExitAsync(this Process process, CancellationToken ct = default)
        {
            var done = new TaskCompletionSource<bool>();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => done.TrySetResult(true);
            if (process.HasExited) done.TrySetResult(true);
            if (ct.CanBeCanceled) ct.Register(() => done.TrySetCanceled());
            return done.Task;
        }
    }
}
#endif
