using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using AdenRising.Updater.Core;
using WinForms = System.Windows.Forms;

namespace AdenRising.Updater;

/// <summary>
/// What the window does once the game is running: it steps aside -- minimised
/// to the taskbar, tray icon up -- and stays, because the server admits only
/// windows this launcher started and reported, the one a player logs into
/// again after a disconnect included. Play becomes "play another window"
/// (dualbox is allowed here, two per computer plus the buffer). The cross
/// minimises while a game runs and explains itself once; Quit is in the tray
/// menu, behind a warning, so nobody is locked out of their own game by
/// closing the wrong window.
/// </summary>
public partial class MainWindow
{
    private readonly List<Running> _games = [];
    private readonly Lock _gamesLock = new();
    private WinForms.NotifyIcon? _tray;

    private void SetUpTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open another game window", null, (_, _) => Dispatcher.Invoke(() => _ = PlayAsync()));
        menu.Items.Add("Show Aden Rising Launcher", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(QuitAsked));

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Aden Rising Launcher",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var stream = Application.GetResourceStream(new Uri("Assets/AdenRising.ico", UriKind.Relative))?.Stream;
        return stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream);
    }

    private bool _playing;
    private bool _explainedStayOpen;

    /// <summary>
    /// Play, from the button and from the tray menu: the integrity check first,
    /// then the launch. The server refuses a client whose files were swapped,
    /// so the launcher says it here, where Verify files is one click away,
    /// rather than letting the player find out after logging in.
    /// </summary>
    private async Task PlayAsync()
    {
        if (_playing) return;
        _playing = true;
        PlayButton.IsEnabled = false;
        try
        {
            // The window may have sat open, or in the tray, since long before the
            // last client release, and Play would start whatever is on disk. A
            // client that old is not one the server or Windows will put up with.
            // Not while a window is already up: its files cannot be replaced,
            // and a second window has to match the first anyway.
            if (!AnyGameRunning() && await ClientOutdatedAsync())
            {
                await CheckAsync();
                return;
            }

            var changed = await Integrity.FirstMismatchAsync(_gameDir, _installer);
            if (changed is not null)
            {
                RestoreFromTray();
                ShowError("Aden Rising", "Changed", "These are not ", "our files",
                    $"{changed} is not the file we installed, and the server does not admit a client whose files were " +
                    "changed. Press Verify files to put the original back.");
                // The fix is one button away, so it is on this screen, and it is the one that stands out.
                VerifyButton.Visibility = Visibility.Visible;
                VerifyButton.Style = (Style)FindResource("BtnPrimary");
                VerifyButton.Margin = default;
                RetryButton.Style = (Style)FindResource("BtnOutline");
                RetryButton.Margin = new Thickness(16, 0, 0, 0);
                return;
            }
            LaunchGame();
        }
        finally
        {
            _playing = false;
            PlayButton.IsEnabled = true;
        }
    }

    /// <summary>Starts a game window. Called by Play and by the tray menu, so a
    /// second one costs the player one click.</summary>
    private bool LaunchGame()
    {
        var exe = Path.Combine(_gameDir, "system", "L2.exe");
        if (!File.Exists(exe))
        {
            RestoreFromTray();
            ShowError("Aden Rising", "Missing", "The game is not ", "there",
                "L2.exe is missing from the folder. Use Verify files to put it back.");
            return false;
        }

        // How the game opens is the client's business now, not ours: full
        // screen, a window, and Alt+Enter between them all belong to it. The
        // only thing we do here is make sure a fresh install has a settings
        // file at all, written for the monitor the player is looking at.
        GameOptions.Correct(_gameDir, Display.ScreenFor(new WindowInteropHelper(this).Handle));

        // This start is ours: the client checks for the token and refuses to run without it.
        Presence.Grant(_gameDir);

        Process game;
        try
        {
            game = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.Combine(_gameDir, "system"),
                UseShellExecute = true,
            })!;
        }
        catch (Exception ex)
        {
            RestoreFromTray();
            ShowError("Aden Rising", "Blocked", "The game would not ", "start", ex.Message);
            return false;
        }

        // Watched by polling rather than Process.Exited: the game asks for
        // administrator rights, and a process running at a higher integrity
        // level does not hand its parent a handle to wait on.
        lock (_gamesLock) _games.Add(new Running(game.Id, Stopwatch.GetTimestamp()));
        StartWatching();

        // Tell the server this window is ours, the moment it connects (AntiBot).
        LaunchReport.Watch(game, _gameDir, _installer.InstalledVersion() ?? "", _http);

        // The launcher stays: minimised to the taskbar, not hidden away. It has
        // to keep running while the game does -- it is what tells the server
        // about every window, the one a player logs into again after a
        // disconnect included -- and a second window is one click from here.
        StepAside(explain: false);
        UpdateTrayText();
        return true;
    }

    /// <summary>
    /// Out of the way, but still there: minimised, in the taskbar, with the
    /// tray icon up as well. Explains itself once when the player tried to
    /// close it with a game running.
    /// </summary>
    private void StepAside(bool explain)
    {
        if (_tray is not null) _tray.Visible = true;
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
        if (explain && !_explainedStayOpen && _tray is not null)
        {
            _explainedStayOpen = true;
            _tray.ShowBalloonTip(8000, "Aden Rising keeps running",
                "The launcher stays open while you play: it is what lets you log in, and log in again after a disconnect. " +
                "Quit is in this icon's menu.", WinForms.ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// A second launcher (a double-click, or L2.exe sending the player here)
    /// cannot bring this window forward itself, so it asks: it sets this named
    /// event and quits, and this one restores itself the way the tray icon does.
    /// </summary>
    private void ListenForShowRequests()
    {
        var listener = new Thread(() =>
        {
            while (true)
            {
                var show = App.ShowEvent;
                if (show is null) return;   // handed off to a self-update
                try { show.WaitOne(); } catch { return; }
                if (App.ShowEvent is null) return;
                Dispatcher.Invoke(async () =>
                {
                    RestoreFromTray();
                    // Opened again from the desktop: to the player this is a fresh start,
                    // so look for a new client as a fresh start would.
                    if (PlayButton.Visibility == Visibility.Visible && !AnyGameRunning() && await ClientOutdatedAsync())
                        await CheckAsync();
                });
            }
        })
        { IsBackground = true, Name = "show-request" };
        listener.Start();
    }

    /// <summary>Quit from the tray menu, with a warning while a game is up.</summary>
    private void QuitAsked()
    {
        if (AnyGameRunning())
        {
            var answer = System.Windows.MessageBox.Show(this,
                "A game window is still running. Without the launcher you cannot log in again in that window: " +
                "after a disconnect you would have to start the game from the launcher once more.\n\nQuit anyway?",
                "Aden Rising", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        QuitForGood();
    }

    /// <summary>One game window we started, and when.</summary>
    private readonly record struct Running(int Pid, long StartedAt);

    private System.Windows.Threading.DispatcherTimer? _watch;

    private void StartWatching()
    {
        if (_watch is not null) return;
        _watch = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _watch.Tick += (_, _) => CheckGames();
        _watch.Start();
    }

    private void CheckGames()
    {
        var finished = new List<Running>();
        List<Running> snapshot;
        lock (_gamesLock) snapshot = [.. _games];

        foreach (var run in snapshot)
        {
            if (!IsAlive(run.Pid)) finished.Add(run);
        }
        if (finished.Count == 0) return;

        int left;
        lock (_gamesLock)
        {
            foreach (var f in finished) _games.Remove(f);
            left = _games.Count;
        }

        UpdateTrayText();
        if (left > 0) return;   // dualbox: wait for the last window

        _watch?.Stop();
        _watch = null;
        RestoreFromTray();

        // Dying within a few seconds is not somebody quitting, it is the game
        // failing to start -- exactly when a player needs telling why.
        var diedAtOnce = finished.Any(f => Stopwatch.GetElapsedTime(f.StartedAt) < TimeSpan.FromSeconds(5));
        if (!diedAtOnce) return;

        // Almost always the graphics translation layer, on a machine whose
        // driver cannot run it: that fails before the client has a window to
        // put an error in, which is why the game simply vanishes. Rather than
        // leave the player to work that out, take it back off and say so.
        // Everything still works without it -- that is how the game ran until
        // today.
        if (!_installer.TranslationRefused && File.Exists(Path.Combine(_gameDir, "system", "d3d9.dll")))
        {
            _installer.RefuseTranslation();
            ShowError("Aden Rising", "Graphics", "That did not agree with your ", "computer",
                "The game would not start with the graphics update in place, so it has been removed. " +
                "Press Play again and it will run without it. Nothing else is affected, and it will not " +
                "be put back.");
            return;
        }

        ShowError("Aden Rising", "Closed", "The game shut straight ", "down",
            "Lineage II started and closed again within a few seconds. Verify files is the usual fix; " +
            "an antivirus blocking the client is the other common cause.");
    }

    // ---- closing the game for the player --------------------------------

    /// <summary>
    /// Every game window running out of <em>our</em> folder.
    ///
    /// Matched on the full path rather than on the process name, which is the
    /// whole point: players are welcome to keep another server's Interlude
    /// installed, and reaching over and closing that one would be exactly the
    /// liberty this launcher promised never to take.
    /// </summary>
    private List<Process> OurRunningGames()
    {
        var ours = Path.Combine(_gameDir, "system", "L2.exe");
        var found = new List<Process>();

        foreach (var p in Process.GetProcessesByName("L2"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch { /* another user's, or elevated past us: not ours to touch */ }

            if (path is not null && path.Equals(ours, StringComparison.OrdinalIgnoreCase))
                found.Add(p);
            else
                p.Dispose();
        }
        return found;
    }

    private bool AnyGameRunning()
    {
        var games = OurRunningGames();
        foreach (var g in games) g.Dispose();
        return games.Count > 0;
    }

    /// <summary>
    /// Closes our game windows and waits for the file handles to go with them.
    /// Returns false if any survived, which on Windows means we were not
    /// allowed to touch it -- worth saying out loud rather than pretending.
    /// </summary>
    private async Task<bool> CloseOurGamesAsync()
    {
        var games = OurRunningGames();
        if (games.Count == 0) return true;

        try
        {
            // Ask before insisting. Lineage II treats a close request the same
            // as Alt+F4 and shuts down cleanly, which lets the login server see
            // the disconnect rather than waiting out the timeout.
            foreach (var g in games)
            {
                try { g.CloseMainWindow(); } catch { }
            }

            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(5) && games.Any(Alive))
                await Task.Delay(200);

            // A client hung badly enough to lock files is often too hung to
            // answer a polite request, and that is the case this button exists
            // for. Nothing is lost by killing it: every bit of a character
            // lives on the server, never on this disk.
            foreach (var g in games.Where(Alive))
            {
                try { g.Kill(); } catch { }
            }

            foreach (var g in games)
            {
                try { await g.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }

            return !games.Any(Alive);
        }
        finally
        {
            foreach (var g in games) g.Dispose();
        }

        static bool Alive(Process p)
        {
            try { return !p.HasExited; }
            catch { return false; }
        }
    }

    private async void CloseGame_Click(object sender, RoutedEventArgs e)
    {
        CloseGameButton.IsEnabled = false;
        CloseGameButton.Content = "CLOSINGâ€¦";

        var closed = await CloseOurGamesAsync();

        CloseGameButton.IsEnabled = true;
        CloseGameButton.Content = "CLOSE THE GAME";

        if (!closed)
        {
            Body.Text = "Windows would not let us close Lineage II. Close it yourself and press Try again.";
            return;
        }

        await CheckAsync();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }   // gone
        catch { return true; }                        // no access: assume running
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        int n;
        lock (_gamesLock) n = _games.Count;
        // The tooltip is capped at 63 characters by the shell.
        _tray.Text = n switch
        {
            0 => "Aden Rising Launcher",
            1 => "Aden Rising â€” one game running",
            _ => $"Aden Rising â€” {n} games running",
        };
        // The same count on the button and in the taskbar, where the player
        // actually looks for it now that the launcher no longer hides.
        PlayButton.Content = n == 0 ? "PLAY" : "PLAY ANOTHER WINDOW";
        Title = n switch
        {
            0 => "Aden Rising",
            1 => "Aden Rising - 1 game window running",
            _ => $"Aden Rising - {n} game windows running",
        };
    }

    private void HideToTray()
    {
        if (_tray is null) return;
        _tray.Visible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        // Windows will not simply hand focus to a background process, and
        // without this nudge the window comes back behind whatever is in front.
        Topmost = true;
        Topmost = false;
        // The icon stays while a game runs: Quit lives there.
        if (_tray is not null) _tray.Visible = AnyGameRunning();
    }

    private void QuitForGood()
    {
        // Before anything else: a cursor still penned into a window that is
        // about to go away leaves the player with a mouse that cannot reach
        // the rest of their desktop.
        _paused = true;
        _cts?.Cancel();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        Presence.Revoke(_gameDir);
        Application.Current.Shutdown();
    }
}

