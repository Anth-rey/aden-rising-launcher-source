using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace AdenRising.Updater.Core;

/// <summary>
/// Tells the server which game windows this launcher started (AntiBot, phase 2).
///
/// The game server cannot tell a client the launcher started from one someone
/// ran by hand, or from a program that only speaks the protocol and never
/// opens the client at all. The launcher can: it started the process, so it
/// knows its id, and Windows will say, for every TCP connection on the
/// machine, which process owns it. So this watches the game it started, and
/// the moment that process holds a connection to the game server it reports
/// the connection's local port -- the same port the server sees the client
/// arriving from -- together with a stable id for this computer and the client
/// build. The server then knows the window came through here, with verified
/// files, and how many windows this computer has open.
///
/// Everything here is ordinary user-level Windows API: the TCP table, the
/// registry key every machine has, the volume serial, process names. Nothing
/// is injected, no other process's memory is read, no driver, no hook. That
/// is deliberate -- an antivirus flags techniques, not intentions, and a
/// launcher that gets quarantined protects nobody.
///
/// Reporting is best effort. A failure here never stops the game: whether a
/// window without a report is allowed in is the server's decision, and it is
/// the server's job to say so to the player.
/// </summary>
public static class LaunchReport
{
    /// <summary>The port the game client talks to the game server on.</summary>
    private const int GamePort = 7777;

    /// <summary>
    /// Shared with the website's LAUNCHER_API_KEY. It ships inside the
    /// launcher, so anyone determined can read it out -- it keeps casual
    /// traffic off the endpoint, nothing more. The real check is that the
    /// reported port has to match a connection the game server actually holds.
    ///
    /// Not in the source: the official build takes it from LaunchKey.local.props
    /// (kept out of git) and bakes it into the assembly's metadata. A build
    /// without it simply reports nothing, and the server then treats its
    /// windows as not started by the launcher.
    /// </summary>
    private static readonly string Key =
        Environment.GetEnvironmentVariable("ADENRISING_LAUNCHER_KEY")
        ?? typeof(LaunchReport).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LauncherKey")?.Value
        ?? "";

    private static readonly string Endpoint =
        (Environment.GetEnvironmentVariable("ADENRISING_SITE_URL") ?? "https://adenrising.com").TrimEnd('/') + "/api/launcher/launch";

    /// <summary>
    /// Programs whose presence a GM wants to know about. Names only, as the
    /// process list shows them, compared without case or extension. Renaming
    /// the file defeats this, which is fine: it is one signal among several,
    /// not the lock on the door.
    /// </summary>
    private static readonly string[] WatchedProcesses =
    [
        "adrenaline", "adrenalinebot", "adrenaline_loader", "adrenalinegui",
        "l2tower", "l2walker", "l2w", "l2net", "l2phx", "l2helper", "l2bot",
        "autohotkey", "autohotkeyu64", "autohotkey64", "autohotkeyu32", "autoit3", "autoit3_x64",
        "cheatengine", "cheatengine-x86_64", "cheatengine-i386",
        "interception", "l2superman", "l2divine", "l2control",
    ];

    /// <summary>
    /// Follows one game process for as long as it lives and reports every
    /// connection it opens to the game server. A player who is disconnected and
    /// logs in again in the same window gets a new local port, and so a new
    /// report, which is exactly what the server needs to let them back in.
    /// </summary>
    public static void Watch(Process game, string gameDir, string clientBuild, HttpClient http)
    {
        _ = Task.Run(async () =>
        {
            var reported = new HashSet<int>();
            string? hwid = null;
            var pid = game.Id;
            while (IsAlive(pid))
            {
                try
                {
                    foreach (var port in LocalPortsToGameServer(pid))
                    {
                        if (!reported.Add(port)) continue;
                        hwid ??= Hwid();
                        await ReportAsync(hwid, clientBuild, game, gameDir, port);
                    }
                }
                catch
                {
                    // The network went away, or a table read failed. The game is
                    // the player's to play; try again on the next tick.
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        });
    }

    /// <summary>
    /// Same shape as the tray's check: the game runs with higher rights than
    /// the launcher, and asking such a process whether it has exited can be
    /// refused. A refusal means it is still there to refuse.
    /// </summary>
    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    /// <summary>
    /// The report has to arrive from the same address the game server sees
    /// the client from. The game speaks IPv4 only, so this client does too:
    /// a site reachable over IPv6 would otherwise record an address the game
    /// server never sees, and the two could never be matched.
    /// </summary>
    private static readonly HttpClient Ipv4Http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (context, ct) =>
        {
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct);
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    private static async Task ReportAsync(string hwid, string clientBuild, Process game, string gameDir, int localPort)
    {
        var http = Ipv4Http;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("x-launcher-key", Key);
            request.Content = JsonContent.Create(new
            {
                hwid,
                clientBuild,
                launcherBuild = LauncherVersion(),
                pid = game.Id,
                localPort,
                signals = Signals(game, gameDir),
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await http.SendAsync(request, cts.Token);
            // A refusal is the server's to explain, in game. Nothing to show here.
        }
        catch
        {
            // Offline, or the site is down: the game still runs; see the class comment.
        }
    }

    private static string LauncherVersion()
    {
        var v = typeof(LaunchReport).Assembly.GetName().Version;
        return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ---- what the launcher can see around the game -------------------------

    private static object Signals(Process game, string gameDir)
    {
        var procs = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (WatchedProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) procs.Add(name);
                }
                finally { p.Dispose(); }
            }
        }
        catch { /* not allowed to list: say nothing rather than guess */ }

        // The modules loaded into the client that are neither the game's own
        // (anything under the install folder) nor Windows'. A library from
        // anywhere else is the plainest trace an in-game bot leaves -- IG
        // L2Walker is one such DLL. The client asks for administrator rights,
        // so a launcher running without them may be refused this list; that
        // is recorded as "unreadable", not as "clean".
        List<string>? modules = null;
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            modules = [];
            foreach (ProcessModule m in game.Modules)
            {
                var file = m.FileName ?? "";
                if (file.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                if (file.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase)) continue;
                modules.Add(Path.GetFileName(file));
            }
        }
        catch { modules = null; }

        return new { procs, modules, modulesReadable = modules is not null };
    }

    // ---- this computer -----------------------------------------------------

    /// <summary>
    /// A stable id for this computer: the machine's own GUID, the serial of
    /// the system drive and the processor string, hashed together. None of it
    /// identifies the person, and it is the same tomorrow and after a
    /// reinstall of the game -- which is all the server wants from it.
    /// </summary>
    private static string Hwid()
    {
        var sb = new StringBuilder();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            sb.Append(key?.GetValue("MachineGuid") as string);
        }
        catch { /* leave it out */ }
        sb.Append('|').Append(SystemDriveSerial());
        sb.Append('|').Append(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"));
        sb.Append('|').Append(Environment.ProcessorCount);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string rootPath, StringBuilder? volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemName, int fileSystemNameSize);

    private static string SystemDriveSerial()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0) ? serial.ToString("x8") : "";
        }
        catch { return ""; }
    }

    // ---- the TCP table -----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint Established = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    /// <summary>
    /// Local ports of every established connection the given process holds to
    /// the game server's port. Read from the table Windows keeps for every
    /// process, which needs no access to the process itself -- it works even
    /// though the client runs with higher rights than the launcher.
    /// </summary>
    private static List<int> LocalPortsToGameServer(int pid)
    {
        var ports = new List<int>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0) return ports;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0) return ports;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();
            var at = buffer + 4;
            for (var i = 0; i < count; i++, at += rowSize)
            {
                var row = Marshal.PtrToStructure<TcpRow>(at);
                if (row.OwningPid != (uint)pid || row.State != Established) continue;
                if (PortOf(row.RemotePort) != GamePort) continue;
                ports.Add(PortOf(row.LocalPort));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return ports;
    }

    /// <summary>The table stores ports in network byte order inside a 32-bit field.</summary>
    private static int PortOf(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}
