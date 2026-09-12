using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace AdenRising.Updater;

public partial class App : Application
{
    /// <summary>
    /// One launcher at a time. A second start -- the player's double-click, or
    /// L2.exe sending them here -- brings the running one to the front and
    /// leaves. Held for the life of the process, handed over for a self-update.
    /// </summary>
    private static Mutex? _single;

    /// <summary>
    /// What a second launcher sets to say "show yourself"; the running one
    /// listens on it (MainWindow.ListenForShowRequests). Owned here so a
    /// self-update can close it before starting its replacement.
    /// </summary>
    public static EventWaitHandle? ShowEvent { get; private set; }

    private const string SingleName = "AdenRisingLauncher";
    public const string ShowEventName = "AdenRisingLauncher.Show";

    protected override void OnStartup(StartupEventArgs e)
    {
        // The window is written in English, so its numbers should read in
        // English too: on a Czech machine the default turned 73.1 MB/s into
        // "73,1 MB/s" while every word around it stayed English.
        // Not InvariantGlobalization in the project file -- WPF cannot start
        // with that on, it needs real cultures for font handling.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // A launcher that listens is a launcher that is up: tell it to show
        // itself and leave at once, no waiting.
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var running))
        {
            using (running) running.Set();
            RaiseTheOther();
            Shutdown();
            return;
        }

        if (!Claim())
        {
            // Held by one that does not listen (an older version, or one still
            // starting): the best that can be done from here.
            RaiseTheOther();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// A self-update starts the new launcher before the old one has quit, so a
    /// taken mutex is given two seconds to free up before it means "already
    /// running".
    /// </summary>
    private static bool Claim()
    {
        _single = new Mutex(false, SingleName);
        bool mine;
        try { mine = _single.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { mine = true; }   // the previous owner died without releasing: ours now
        if (!mine) return false;

        try { ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
        catch { ShowEvent = null; }
        return true;
    }

    /// <summary>
    /// Before a self-update starts its replacement: stop being "the running
    /// launcher", or the new one would wake this one and quit, leaving the
    /// player with the old version about to close.
    /// </summary>
    public static void HandOff()
    {
        try { ShowEvent?.Dispose(); } catch { }
        ShowEvent = null;
        try { _single?.ReleaseMutex(); } catch { }
        try { _single?.Dispose(); } catch { }
        _single = null;
    }

    /// <summary>The self-update did not go through after all: be the running launcher again.</summary>
    public static void Reclaim()
    {
        if (_single is null) Claim();
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Restore and foreground the running launcher's window from here. Windows
    /// grants the foreground grudgingly, but a process the user just started
    /// (or one started by the process they were using) is usually allowed.
    /// </summary>
    private static void RaiseTheOther()
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                using (p)
                {
                    if (p.Id == me || p.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                }
            }
        }
        catch { }
    }
}
