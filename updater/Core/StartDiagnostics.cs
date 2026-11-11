using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AdenRising.Updater.Core;

/// <summary>
/// What a computer looks like where the game sat at start-up with no window: the facts that tell such computers apart
/// from the ones where it opens (2026-11-11: two players on Windows 10 stuck, every other client working for them).
///
/// Gathered once, at the first stuck start, and sent with that report (LaunchReport.ReportStart). Only things that
/// describe the machine and the stuck game, never the person: no names, paths, serials or ids. Process and module
/// names only, as Windows lists them. Everything is read without asking for more rights than the launcher has; a
/// value that cannot be read is left out rather than guessed.
/// </summary>
public static class StartDiagnostics
{
    public static object Collect(Process? game, string gameDir)
    {
        var d = new Dictionary<string, object?>();
        Try(() => Windows(d));
        Try(() => Cpu(d));
        Try(() => Memory(d));
        Try(() => Gpu(d));
        Try(() => Security(d));
        Try(() => d["processes"] = Processes());
        Try(() => d["extraFiles"] = ExtraFiles(gameDir));
        if (game is not null) Try(() => Game(d, game, gameDir));
        return d;
    }

    private static void Try(Action read)
    {
        try { read(); } catch { /* left out */ }
    }

    private static string? Reg(string key, string name) => Registry.GetValue(key, name, null)?.ToString();

    private static void Windows(Dictionary<string, object?> d)
    {
        const string nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        d["winProduct"] = Reg(nt, "ProductName");
        d["winEdition"] = Reg(nt, "EditionID");
        d["winRelease"] = Reg(nt, "DisplayVersion") ?? Reg(nt, "ReleaseId");
        d["winBuild"] = $"{Reg(nt, "CurrentBuild")}.{Reg(nt, "UBR")}";
        d["winInstallType"] = Reg(nt, "InstallationType");
        d["is64"] = Environment.Is64BitOperatingSystem;
        d["locale"] = System.Globalization.CultureInfo.CurrentCulture.Name;
        d["codepage"] = System.Text.Encoding.Default.CodePage;
        d["ansiCodepage"] = GetACP();
    }

    private static void Cpu(Dictionary<string, object?> d)
    {
        const string cpu = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        d["cpu"] = Reg(cpu, "ProcessorNameString")?.Trim();
        d["cpuVendor"] = Reg(cpu, "VendorIdentifier");
        d["threads"] = Environment.ProcessorCount;
    }

    private static void Memory(Dictionary<string, object?> d)
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref m)) d["ramGb"] = Math.Round(m.TotalPhys / 1073741824.0, 1);
    }

    private static void Gpu(Dictionary<string, object?> d)
    {
        var gpus = new List<string>();
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return;
        foreach (var sub in cls.GetSubKeyNames())
        {
            if (!int.TryParse(sub, out _)) continue;
            using var k = cls.OpenSubKey(sub);
            var name = k?.GetValue("DriverDesc")?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            gpus.Add($"{name} ({k?.GetValue("DriverVersion")})");
        }
        d["gpus"] = gpus.Distinct().Take(6).ToList();
    }

    private static void Security(Dictionary<string, object?> d)
    {
        const string dg = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard";
        d["vbsConfigured"] = Reg(dg, "EnableVirtualizationBasedSecurity");
        d["hvci"] = Reg(dg + @"\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        d["smartAppControl"] = Reg(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState");
        d["hypervisorLaunch"] = Reg(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel", "HypervisorRunning");
        d["defenderDisabled"] = Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware");
        d["dep"] = GetSystemDEPPolicy();
    }

    /// <summary>Distinct names of everything running, so an overlay, an antivirus or a tool sitting on every stuck computer shows up.</summary>
    private static List<string> Processes()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName.Length > 40 ? p.ProcessName[..40] : p.ProcessName); }
            catch { }
            finally { p.Dispose(); }
        }
        return names.Take(300).ToList();
    }

    /// <summary>Libraries in the game's system folder that we did not put there (a d3d9.dll from a tool, an old patch).</summary>
    private static List<string> ExtraFiles(string gameDir)
    {
        var system = Path.Combine(gameDir, "system");
        var manifest = Path.Combine(gameDir, ".updater", "installed-manifest.json");
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var m = Manifest.Parse(File.ReadAllText(manifest));
            foreach (var f in m.Files) ours.Add(Path.GetFileName(f.Path));
        }
        catch { return []; }
        return Directory.EnumerateFiles(system)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(n => n is not null && !ours.Contains(n))
            .Select(n => n!)
            .Take(50)
            .ToList();
    }

    /// <summary>The stuck game itself: how far it got (memory, threads, processor time) and what it has loaded.</summary>
    private static void Game(Dictionary<string, object?> d, Process game, string gameDir)
    {
        game.Refresh();
        d["gameWorkingSetMb"] = Math.Round(game.WorkingSet64 / 1048576.0, 1);
        d["gamePrivateMb"] = Math.Round(game.PrivateMemorySize64 / 1048576.0, 1);
        d["gameThreads"] = game.Threads.Count;
        try { d["gameCpuSeconds"] = Math.Round(game.TotalProcessorTime.TotalSeconds, 1); } catch { }
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var ours = new List<string>();
            var foreign = new List<string>();
            var system = 0;
            foreach (ProcessModule m in game.Modules)
            {
                var file = m.FileName ?? "";
                var name = Path.GetFileName(file);
                if (file.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase)) ours.Add(name);
                else if (file.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) system++;
                else foreign.Add(name);
            }
            d["gameModulesOurs"] = ours;
            d["gameModulesForeign"] = foreign;
            d["gameModulesSystem"] = system;
        }
        catch { d["gameModulesReadable"] = false; }
    }

    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("kernel32.dll")] private static extern int GetSystemDEPPolicy();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
