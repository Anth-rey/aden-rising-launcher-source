using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AdenRising.Updater.Core;

namespace AdenRising.Updater;

public sealed record NewsRow(string Date, string Title, string Url);

public partial class MainWindow : Window
{
    /// <summary>
    /// Where the client comes from, and where the launcher fetches its own
    /// replacement. Deliberately two hosts: files.adenrising.net carries the
    /// game and nothing else. Both must end in a slash.
    ///
    /// In a Debug build only, ADENRISING_FILES_URL and ADENRISING_LAUNCHER_URL
    /// point these at tools/serve-mirror.mjs, which exists to break the update
    /// path on purpose -- dropped connections, throttled speeds, files that
    /// vanish mid-release. A released launcher ignores the environment: a
    /// variable any program on the machine can set must not decide where the
    /// launcher takes its updates from. (Everything fetched is signed anyway;
    /// this closes the door twice.)
    /// </summary>
    private static readonly string BaseUrl = Override("ADENRISING_FILES_URL") ?? "https://files.adenrising.net/";
    private static readonly string LauncherUrl = Override("ADENRISING_LAUNCHER_URL") ?? "https://download.adenrising.com/";

    /// <summary>
    /// True when the override above sent us somewhere other than the real
    /// bucket. A test run installs into a scratch folder, and if it were
    /// allowed to remember that folder it would leave the launcher pointing at
    /// it afterwards -- which then looks to the player like their game has
    /// vanished and 6 GB is being fetched all over again. It happened.
    /// </summary>
    private static readonly bool UsingTestMirror = Override("ADENRISING_FILES_URL") is { Length: > 0 };

    private static string? Override(string variable)
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(variable);
#else
        return null;
#endif
    }
    private const string SiteUrl = "https://adenrising.com";
    private const string DefaultFolder = @"C:\Games\Aden Rising";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SpeedMeter _speed = new();

    private string _gameDir;
    private Installer _installer = null!;
    private Manifest? _manifest;
    private string _manifestIssuedAt = "";
    private InstallPlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _paused;
    private ServerStatus _status = ServerStatus.Unknown;

    public MainWindow()
    {
        InitializeComponent();

        NewsHeading.Text = Track("LATEST NEWS", 0.32);
        AllNewsLabel.Text = Track("ALL NEWS", 0.32);

        var args = Environment.GetCommandLineArgs();
        // Skip switches and anything that is a switch's value, so a folder
        // passed on the command line is still found when one is present.
        var firstPath = FirstPathArgument(args);
        _gameDir = firstPath is not null ? Path.GetFullPath(firstPath) : Settings.ReadLocation() ?? DefaultFolder;
        Rebind(_gameDir);


        SetUpTray();
        ListenForShowRequests();

        // CenterScreen puts the window on whichever monitor the pointer happens
        // to be on. On a desk with two screens that is a coin toss, so the
        // primary one is chosen deliberately.
        SourceInitialized += (_, _) => CentreOnPrimary();

        Loaded += async (_, _) =>
        {
            SelfUpdate.ForgetPrevious();
            if (await TrySelfUpdateAsync()) return;   // we are being replaced

            _ = RefreshStatusAsync();
            _ = RefreshNewsAsync();
            // Keep it current: the window can sit in the tray for a whole
            // evening while somebody plays, and a player count from when they
            // pressed Play is not worth showing.
            var beat = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30),
            };
            beat.Tick += (_, _) => _ = RefreshStatusAsync();
            beat.Start();

            await CheckAsync();
        };
    }

    private static string? FirstPathArgument(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) return args[i];
            i++;   // step over the switch's value
        }
        return null;
    }

    private void CentreOnPrimary()
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
        if (area is null) return;

        // WorkingArea is in real pixels; Left/Top are in WPF units, and on a
        // scaled display those are not the same number.
        var toWpf = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                    ?? System.Windows.Media.Matrix.Identity;
        var origin = toWpf.Transform(new System.Windows.Point(area.Value.Left, area.Value.Top));
        var size = toWpf.Transform(new System.Windows.Point(area.Value.Width, area.Value.Height));

        Left = origin.X + (size.X - Width) / 2;
        Top = origin.Y + (size.Y - Height) / 2;
    }

    private void Rebind(string dir)
    {
        _gameDir = dir;
        var blobs = new BlobStore(_http, new Uri(BaseUrl), Path.Combine(dir, ".updater", "cache"));
        _installer = new Installer(dir, blobs);
        // A token left behind by a crash would admit one hand-started window; clear it.
        Presence.Revoke(dir);
    }

    // ---- the site's typographic details ---------------------------------

    /// <summary>
    /// WPF has no letter-spacing, and the site's display type lives on it --
    /// eyebrows are set at 0.32em. Thin spaces between the letters get close
    /// enough at these sizes, and only ever apply to short runs of capitals.
    /// </summary>
    private static string Track(string text, double em)
    {
        var gap = em >= 0.3 ? "\u2009" : "\u200A";
        return string.Join(gap, text.ToCharArray());
    }

    /// <summary>
    /// Headings on the site carry one word in crimson (h1 em). It is the single
    /// detail that makes a line of type look like it belongs to this project.
    /// </summary>
    private void SetHeadline(string plain, string accent = "")
    {
        Headline.Inlines.Clear();
        Headline.Inlines.Add(new Run(plain));
        if (accent.Length == 0) return;
        Headline.Inlines.Add(new Run(accent) { Foreground = (Brush)FindResource("Crimson") });
    }

    private void SetEyebrow(string lead, string tail, string brushKey = "Crimson")
    {
        var brush = (Brush)FindResource(brushKey);
        EyebrowLead.Text = Track(lead.ToUpperInvariant(), 0.32);
        EyebrowTail.Text = Track(tail.ToUpperInvariant(), 0.32);
        EyebrowLead.Foreground = brush;
        EyebrowTail.Foreground = brush;
        EyebrowRule.Visibility = tail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FadeIn()
    {
        Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ---- screens --------------------------------------------------------

    private void HideAll()
    {
        FolderPanel.Visibility = Visibility.Collapsed;
        TrackRow.Visibility = Visibility.Collapsed;
        StatsRow.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
        VerifyButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        CloseGameButton.Visibility = Visibility.Collapsed;
        ShortcutCheck.Visibility = Visibility.Collapsed;
        Percent.Text = "";
        Body.Text = "";
    }

    private void ShowChecking()
    {
        HideAll();
        SetEyebrow("Aden Rising", "Checking");
        SetHeadline("Looking for ", "updates");
        SetStats("Server status", "—", "Speed", "—", "Client", "—");
        FadeIn();
    }

    /// <summary>
    /// On a fresh machine nothing is fetched until the player has seen where it
    /// will go and how big it is, and said go. The updater is a guest on their
    /// disk, not the other way round.
    /// </summary>
    private void ShowFirstRun()
    {
        HideAll();
        SetEyebrow("First run", "Setup");
        SetHeadline("Choose where Aden Rising ", "goes");
        Body.Text = "We bring our own complete Interlude client. Anything else you have installed is left alone — we never go looking for it.";
        FolderPanel.Visibility = Visibility.Visible;
        InstallButton.Visibility = Visibility.Visible;
        ShortcutCheck.Visibility = Visibility.Visible;
        FolderText.Text = _gameDir;
        UpdateSpaceLine();
        SetStats("To download", Human(_manifest?.TotalBytes ?? 0), "Players online", _status.Players_, "Version", _manifest?.Version ?? "—");
        FadeIn();
    }

    private void ShowWorking(string lead, string tail, string plain, string accent)
    {
        HideAll();
        SetEyebrow(lead, tail, "GoldDark");
        SetHeadline(plain, accent);
        TrackRow.Visibility = Visibility.Visible;
        StatsRow.Visibility = Visibility.Visible;
        PauseButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        PauseButton.Content = "PAUSE";
        PauseButton.Tag = "noarrow";
        FadeIn();
    }

    private void ShowPaused()
    {
        SetEyebrow("Paused", "Waiting", "GoldDark");
        SetHeadline("Stopped for ", "now");
        PauseButton.Content = "RESUME";
        PauseButton.Tag = null;
        Stat2Value.Text = "—";   // one character, nothing to track
        RightStat.Text = "picks up from here when you resume";
    }

    private void ShowReady(string version)
    {
        HideAll();
        SetEyebrow("Aden Rising", "Ready", "GoldDark");
        SetHeadline("The world is ", "waiting");
        Body.Text = "Your client is up to date. If anything ever looks wrong in game, "
                  + "Verify files checks every file against the server and repairs what does not match.";
        PlayButton.Visibility = Visibility.Visible;
        VerifyButton.Visibility = Visibility.Visible;
        // Back to its quiet self after the "not our files" screen made it the main button.
        VerifyButton.Style = (Style)FindResource("BtnOutline");
        VerifyButton.Margin = new Thickness(16, 0, 0, 0);
        // Both versions in one stat: the launcher's used to be findable only in
        // the file's properties, and support keeps asking for it.
        SetStats("Server status", _status.ServerText, "Players online", _status.Players_,
                 "Client / launcher", $"{version} / {SelfUpdate.Current.ToString(3)}", serverOnline: _status.Online);
        FadeIn();
    }

    /// <param name="offerClose">Put a button on the screen that closes the game
    /// for the player. Only worth offering when the game really is what is in
    /// the way, and only when we can actually see it running.</param>
    private void ShowError(string lead, string tail, string plain, string accent, string body,
                           bool offerClose = false)
    {
        HideAll();
        SetEyebrow(lead, tail);
        SetHeadline(plain, accent);
        Body.Text = body;
        RetryButton.Visibility = Visibility.Visible;

        // Two crimson buttons side by side would both read as the thing to
        // press. Closing the game is the one that gets the player moving, so
        // it takes the emphasis and Try again steps back to an outline.
        var canClose = offerClose && AnyGameRunning();
        CloseGameButton.Visibility = canClose ? Visibility.Visible : Visibility.Collapsed;
        CloseGameButton.IsEnabled = true;
        CloseGameButton.Content = "CLOSE THE GAME";
        RetryButton.Style = (Style)FindResource(canClose ? "BtnOutline" : "BtnPrimary");
        RetryButton.Margin = canClose ? new Thickness(16, 0, 0, 0) : default;
        // Whatever went wrong, the world is still whatever it is: a game that
        // would not launch says nothing about the server, and blanking a number
        // we already know is just losing information.
        SetStats("Server status", _status.ServerText, "Players online", _status.Players_,
                 "Client", _installer.InstalledVersion() ?? "—", serverOnline: _status.Online);
        FadeIn();
    }

    /// <param name="serverOnline">Null when the first stat is not the realm at
    /// all, which is most screens. Otherwise it decides the dot.</param>
    private void SetStats(string l1, string v1, string l2, string v2, string l3, string v3,
                          bool? serverOnline = null)
    {
        // The site sets this strip at 0.25em on the label and 0.05em on the
        // value. A hair space is about 0.04em, which is as close as thin spaces
        // get and near enough at these sizes.
        Stat1Label.Text = Track(l1.ToUpperInvariant(), 0.25); Stat1Value.Text = Track(v1, 0.05);
        Stat2Label.Text = Track(l2.ToUpperInvariant(), 0.25); Stat2Value.Text = Track(v2, 0.05);
        Stat3Label.Text = Track(l3.ToUpperInvariant(), 0.25); Stat3Value.Text = Track(v3, 0.05);

        // Straight from the site rate-bar: the realm colours the word itself,
        // green when it is up and crimson when it is not. Every other stat in
        // this strip is ivory.
        Stat1Value.Foreground = (Brush)FindResource(serverOnline switch
        {
            true  => "Online",
            false => "Crimson",
            null  => "Ivory",
        });
    }

    private void UpdateSpaceLine()
    {
        var needed = _manifest?.TotalBytes ?? 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_gameDir))!;
            var free = new DriveInfo(root).AvailableFreeSpace;
            var short_ = free < needed;
            SpaceText.Text = short_
                ? $"{Human(free)} free on {root} — {Human(needed - free)} short"
                : $"{Human(free)} free on {root}";
            SpaceText.Foreground = (Brush)FindResource(short_ ? "Crimson" : "Muted");
            InstallButton.IsEnabled = !short_;
        }
        catch
        {
            SpaceText.Text = "";
            InstallButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Real numbers or none. A launcher that shows a plausible-looking player
    /// count it made up is lying to the player about the thing they came to
    /// check, so an unreachable site leaves a dash.
    /// </summary>
    private async Task RefreshStatusAsync()
    {
        try
        {
            using var quick = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var json = await quick.GetStringAsync($"{SiteUrl}/api/status");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            _status = new ServerStatus(
                root.TryGetProperty("online", out var o) && o.GetBoolean(),
                root.TryGetProperty("playersOnline", out var p) ? p.GetInt32() : null);
        }
        catch
        {
            _status = ServerStatus.Unknown;
        }

        // Repaint whatever screen is showing, now that the numbers are real.
        Dispatcher.Invoke(() =>
        {
            if (PlayButton.Visibility == Visibility.Visible)
                SetStats("Server status", _status.ServerText, "Players online", _status.Players_,
                         "Client", _installer.InstalledVersion() ?? "—", serverOnline: _status.Online);
            else if (InstallButton.Visibility == Visibility.Visible && _manifest is not null)
                SetStats("To download", Human(_manifest.TotalBytes), "Players online", _status.Players_,
                         "Version", _manifest.Version);
        });
    }

    // ---- the work -------------------------------------------------------

    /// <summary>
    /// The three latest headlines from the site. Same rule as the player count:
    /// what is really there or nothing at all. The panel is empty until this
    /// answers, and stays empty if the site cannot be reached -- three invented
    /// headlines about patches that never shipped is not a nicer failure, it is
    /// a lie the player has no way to catch.
    /// </summary>
    private async Task RefreshNewsAsync()
    {
        List<NewsRow> rows;
        try
        {
            using var quick = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var json = await quick.GetStringAsync($"{SiteUrl}/api/news");
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            rows = [];
            foreach (var item in doc.RootElement.GetProperty("articles").EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var slug = item.TryGetProperty("slug", out var g) ? g.GetString() : null;
                var url = string.IsNullOrEmpty(slug) ? $"{SiteUrl}/news" : $"{SiteUrl}/news/{slug}";

                rows.Add(new NewsRow(NewsDate(item), title, url));
            }
        }
        catch
        {
            return;   // leave the panel as it is rather than emptying it
        }

        Dispatcher.Invoke(() =>
        {
            NewsList.ItemsSource = rows;
            NewsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>"22 AUG", tracked like every other eyebrow on the window.</summary>
    private static string NewsDate(System.Text.Json.JsonElement item)
    {
        if (item.TryGetProperty("publishedAt", out var p)
            && p.ValueKind == System.Text.Json.JsonValueKind.String
            && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal, out var when))
        {
            return Track(when.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture).ToUpperInvariant(), 0.32);
        }
        return "";
    }

    /// <summary>
    /// Replaces the launcher before it does anything else, so a player never
    /// runs a version that cannot read the current manifest. Returns true when
    /// a replacement has been started and this process is on its way out.
    ///
    /// Everything here fails quietly on purpose. Not being able to update the
    /// launcher is no reason to stop somebody playing.
    /// </summary>
    private async Task<bool> TrySelfUpdateAsync()
    {
        var baseUrl = new Uri(LauncherUrl);
        var release = await SelfUpdate.CheckAsync(_http, baseUrl);
        if (release is null) return false;

        ShowWorking("Launcher", "Please wait", "Updating the ", "launcher");
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        RightStat.Text = "";
        SetStats("New version", release.Version, "Size", Human(release.Size), "Current",
                 SelfUpdate.Current.ToString(3));

        var progress = new Progress<(long Done, long Total)>(p =>
        {
            var fraction = p.Total <= 0 ? 0 : Math.Clamp((double)p.Done / p.Total, 0, 1);
            Percent.Text = $"{(int)(fraction * 100)}%";
            Fill.BeginAnimation(WidthProperty, null);
            Fill.Width = TrackRow.Width * fraction;
            LeftStat.Text = $"{Human(p.Done)} of {Human(p.Total)}";
        });

        // The replacement must not find us listening, or it would wake us and quit.
        App.HandOff();
        var replaced = await SelfUpdate.ApplyAsync(_http, baseUrl, release,
            (done, total) => ((IProgress<(long, long)>)progress).Report((done, total)));

        if (replaced)
        {
            QuitForGood();
            return true;
        }
        App.Reclaim();

        // Could not put it in place -- most likely the launcher sits somewhere
        // the player cannot write to. Carry on with the one we have.
        return false;
    }

    private async Task CheckAsync()
    {
        ShowChecking();
        try
        {
            // Signed, or it is not a manifest (Signing). The plain
            // manifests/latest.json older launchers read is left where it is.
            var json = await Retry.OnNetworkAsync(
                token => _http.GetStringAsync(new Uri(new Uri(BaseUrl), "manifests/latest.v2.json"), token));
            var (manifest, issuedAt) = Manifest.ParseSigned(json);
            TrustLog.RequireNotOlder("manifest", issuedAt);
            _manifest = manifest;
            _manifestIssuedAt = issuedAt;

            // Our own record is the fast path, but it must not be the only
            // thing we trust: if it is missing while a client is plainly
            // sitting there, offering a fresh install means downloading six
            // gigabytes on top of six gigabytes. Look at the folder instead.
            if (_installer.InstalledVersion() is null && !LooksInstalled())
            {
                ShowFirstRun();
                return;
            }

            await PlanAndApplyAsync(verify: false);
        }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Is there a game in this folder, whatever our records say?</summary>
    private bool LooksInstalled() => File.Exists(Path.Combine(_gameDir, "system", "L2.exe"));

    private async Task PlanAndApplyAsync(bool verify)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _paused = false;

        try
        {
            var total = _manifest!.Files.Count;
            var showing = verify;

            if (verify)
            {
                ShowWorking("Verifying", "Please wait", "Checking your game ", "files");
                PauseButton.Visibility = Visibility.Collapsed;
                LeftStat.Text = "";
                RightStat.Text = "";
            }

            // Planning is usually instant -- it reads our own record of the last
            // install. But it hashes the whole folder on a verify, and again on
            // an install that was cut off part way, and both take minutes. That
            // is far too long to sit on a still screen looking hung.
            var progress = new Progress<int>(n =>
            {
                if (!showing)
                {
                    // A plan that never gets this far finished in a blink, and
                    // flashing a progress bar for that is worse than nothing.
                    if (n < 64) return;
                    showing = true;
                    SetEyebrow("Checking", "Please wait", "GoldDark");
                    SetHeadline("Looking over your game ", "files");
                    TrackRow.Visibility = Visibility.Visible;
                    StatsRow.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Visible;
                }
                PaintChecked(n, total);
            });

            _plan = await Task.Run(() => _installer.PlanAsync(_manifest!, verify, progress, ct), ct);
            if (_plan.NothingToDo)
            {
                // Nothing moved, but this may still be a newer manifest.
                await _installer.RecordAsync(_manifest!);
                TrustLog.Accept("manifest", _manifestIssuedAt);
                // An install made before the launcher started keeping a copy of
                // itself in the game folder has nothing to move either, so this
                // is the only path by which those players ever get one.
                Settle(shortcutsAsked: false);
                ShowReady(_manifest!.Version);
                return;
            }
            await ApplyAsync();
        }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Progress while files are being read rather than fetched. Repaints
    /// in steps because this fires once per file, 1,457 times over.</summary>
    private void PaintChecked(int n, int total)
    {
        if (n % 16 != 0 && n != total) return;

        var fraction = total == 0 ? 1 : (double)n / total;
        Percent.Text = $"{(int)(fraction * 100)}%";
        Fill.BeginAnimation(WidthProperty, null);
        Fill.Width = TrackRow.Width * Math.Clamp(fraction, 0, 1);
        LeftStat.Text = $"{n:N0} of {total:N0} files checked";
    }

    private async Task ApplyAsync()
    {
        var manifest = _manifest!;
        var plan = _plan!;
        var fresh = _installer.InstalledVersion() is null;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _paused = false;

        if (fresh) ShowWorking("Installing", "Please wait", "Downloading game ", "files");
        else ShowWorking("Updating", "Please wait", $"Getting {manifest.Version} ", "ready");

        _speed.Reset();
        long done = 0;
        var files = 0;
        var lastPaint = Stopwatch.StartNew();

        void Paint()
        {
            var fraction = Math.Clamp(plan.Bytes == 0 ? 1 : (double)done / plan.Bytes, 0, 1);
            var left = Math.Max(0, plan.Bytes - done);
            Percent.Text = $"{(int)(fraction * 100)}%";

            // Animated rather than set, so the bar glides instead of stepping.
            Fill.BeginAnimation(WidthProperty, new DoubleAnimation(
                TrackRow.Width * Math.Clamp(fraction, 0, 1), TimeSpan.FromMilliseconds(240))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            LeftStat.Text = $"{files:N0} of {plan.Work.Count:N0} files   ·   {Human(Math.Min(done, plan.Bytes))} of {Human(plan.Bytes)}";

            var bps = _speed.BytesPerSecond;
            Stat1Label.Text = Track(fresh ? "TO DOWNLOAD" : "TO UPDATE", 0.25);
            Stat1Value.Text = Track(Human(left), 0.05);
            Stat1Value.Foreground = (Brush)FindResource("Ivory");
            Stat2Label.Text = Track("SPEED", 0.25);
            Stat2Value.Text = Track(bps > 0 ? $"{Human(bps)}/s" : "—", 0.05);
            Stat3Label.Text = Track("VERSION", 0.25);
            Stat3Value.Text = Track(manifest.Version, 0.05);
            RightStat.Text = bps > 0 && left > 0 ? $"about {Remaining(left, bps)} left" : "";
        }

        Paint();

        try
        {
            await Task.Run(() => _installer.ApplyAsync(plan,
                onBytes: n =>
                {
                    Interlocked.Add(ref done, n);
                    _speed.Add(n);
                    if (lastPaint.ElapsedMilliseconds < 140) return;
                    lastPaint.Restart();
                    Dispatcher.Invoke(Paint);
                },
                onFile: n => files = n,
                ct: ct), ct);

            done = plan.Bytes;
            Paint();

            TrustLog.Accept("manifest", _manifestIssuedAt);
            if (!UsingTestMirror) Settings.WriteLocation(_gameDir);
            Settle(shortcutsAsked: ShortcutCheck.IsChecked == true);

            ShowReady(manifest.Version);
        }
        catch (OperationCanceledException) when (_paused) { ShowPaused(); }
        catch (Exception ex) { Report(ex); }
    }

    private void Report(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return;
            case HttpRequestException:
                // Careful with the wording: the stat strip below says whether the
                // *realm* is up, and those are two different machines. Calling
                // this one "offline" next to a green ONLINE read as nonsense.
                ShowError("Updates", "Unreachable", "We could not reach the ", "update server",
                    "Either your connection is down or the update server is being worked on. The game itself may be perfectly fine, and your files are untouched.");
                return;
            case IOException io when io.Message.Contains("another process"):
                ShowError("Aden Rising", "In use", "The game is still ", "open",
                    "We cannot replace files while Lineage II has them open. Close it and this carries on from where it stopped.",
                    offerClose: true);
                return;
            default:
                LogFailure(ex);
                ShowError("Aden Rising", "Problem", "That did not ", "finish",
                    "Something unexpected went wrong. We have written the details to "
                    + @".updater\last-error.txt inside your game folder — sending us that file "
                    + "is the quickest way to get it fixed.");
                return;
        }
    }

    /// <summary>
    /// Technical detail belongs where it can be read later, not on a screen a
    /// player is trying to get past. A stack trace helps nobody standing in
    /// front of it, and throwing it away helps us even less.
    /// </summary>
    private void LogFailure(Exception ex)
    {
        try
        {
            var dir = Path.Combine(_gameDir, ".updater");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last-error.txt"),
                $"{DateTime.Now:u}{Environment.NewLine}{_manifest?.Version ?? "no manifest"}{Environment.NewLine}{ex}");
        }
        catch { /* if even this fails, saying so on screen would not help */ }
    }

    /// <summary>
    /// Everything that makes the install findable again tomorrow: the copy of
    /// the launcher in the game folder, and the shortcuts that point at it.
    ///
    /// Runs on every launch once the game is ready, not only on a fresh
    /// install, because the players this fixes are the ones who installed
    /// months ago and have since cleared their downloads.
    /// </summary>
    private void Settle(bool shortcutsAsked)
    {
        // A run against the local test mirror is not somebody's real install
        // and has no business writing to their desktop.
        if (UsingTestMirror) return;

        var launcher = Install.PlaceInGameFolder(_gameDir);
        Install.CreateShortcuts(launcher, _gameDir, force: shortcutsAsked);
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser, nothing we can do about it */ }
    }

    // ---- handlers -------------------------------------------------------

    private void Titlebar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Centre_Click(object sender, RoutedEventArgs e) => CentreOnPrimary();

    /// <summary>
    /// The cross means quit -- unless a game is running, when the launcher has
    /// to stay for the player's own sake (see StepAside). Quit is then in the
    /// tray menu, behind a warning.
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (AnyGameRunning()) { StepAside(explain: true); return; }
        QuitForGood();
    }

    private void News_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) Open(url);
    }

    private void AllNews_Click(object sender, RoutedEventArgs e) => Open($"{SiteUrl}/news");

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where should Aden Rising go?",
            InitialDirectory = Directory.Exists(_gameDir) ? _gameDir : @"C:\",
        };
        if (dialog.ShowDialog(this) != true) return;

        var chosen = dialog.FolderName;
        // Picking "Games" should not scatter the client across it.
        if (!Path.GetFileName(chosen.TrimEnd(Path.DirectorySeparatorChar))
                .Equals("Aden Rising", StringComparison.OrdinalIgnoreCase))
        {
            chosen = Path.Combine(chosen, "Aden Rising");
        }

        Rebind(chosen);
        FolderText.Text = chosen;
        UpdateSpaceLine();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _plan = await Task.Run(() => _installer.PlanAsync(_manifest!, false));
        await ApplyAsync();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_paused) { await ResumeAsync(); return; }
        _paused = true;
        _cts?.Cancel();
    }

    /// <summary>
    /// Resuming re-plans instead of replaying the plan made before the pause.
    /// Files finished earlier were moved out of the cache onto their real path,
    /// so the old plan still lists them and would fetch every one again.
    /// </summary>
    private async Task ResumeAsync()
    {
        ShowWorking("Resuming", "Please wait", "Picking up where it ", "stopped");
        _plan = await Task.Run(() => _installer.PlanAsync(_manifest!, false));
        if (_plan.NothingToDo)
            {
                // Nothing moved, but this may still be a newer manifest.
                await _installer.RecordAsync(_manifest!);
                TrustLog.Accept("manifest", _manifestIssuedAt);
                ShowReady(_manifest!.Version);
                return;
            }
        await ApplyAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _paused = true;
        _cts?.Cancel();
        var installed = _installer.InstalledVersion();
        if (installed is null) ShowFirstRun();
        // Stopped half way to a newer client: offering Play here would start the old one.
        else if (_manifest is not null && installed != _manifest.Version)
            ShowError("Aden Rising", "Not finished", "The update is not ", "finished",
                "The game needs the whole update before it can start. Press Try again to finish it; "
                + "everything already downloaded is kept.");
        else ShowReady(installed);
    }

    /// <summary>
    /// True when the server holds a newer client than the one on disk. Anything
    /// that goes wrong asking says false: an unreachable update server is no
    /// reason to stop somebody playing what they have.
    /// </summary>
    private async Task<bool> ClientOutdatedAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var json = await _http.GetStringAsync(new Uri(new Uri(BaseUrl), "manifests/latest.v2.json"), cts.Token);
            var (manifest, _) = Manifest.ParseSigned(json);
            return manifest.Version != _installer.InstalledVersion();
        }
        catch
        {
            return false;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async void Verify_Click(object sender, RoutedEventArgs e) => await PlanAndApplyAsync(verify: true);

    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayAsync();

    // ---- formatting -----------------------------------------------------

    private static string Human(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return string.Create(CultureInfo.InvariantCulture, $"{bytes:0.#} {units[u]}");
    }

    private static string Remaining(double bytesLeft, double bps)
    {
        var seconds = bytesLeft / Math.Max(1, bps);
        if (seconds < 60) return Plural((int)seconds, "second");
        if (seconds < 3600) return Plural((int)(seconds / 60), "minute");
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:0.#} hours");

        static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
    }
}

/// <summary>Where the player put the game, so the next launch does not ask again.</summary>
internal static class Settings
{
    private static string Path_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aden Rising", "location.txt");

    public static string? ReadLocation()
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            var dir = File.ReadAllText(Path_).Trim();
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    public static void WriteLocation(string dir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, dir);
        }
        catch { /* remembering is a convenience, not a requirement */ }
    }
}

/// <summary>
/// Speed over the last few seconds, not since the start. A running average
/// makes a stall look fine for a minute and a recovery look slow for another;
/// what a player wants to see is what is happening now.
/// </summary>
internal sealed class SpeedMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private readonly Lock _lock = new();
    private long _total;

    public void Reset() { lock (_lock) { _samples.Clear(); _total = 0; } }

    public void Add(long bytes)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _samples.Enqueue((now, bytes));
            _total += bytes;
            var cutoff = now - (long)(Window.TotalSeconds * Stopwatch.Frequency);
            while (_samples.Count > 0 && _samples.Peek().Ticks < cutoff)
                _total -= _samples.Dequeue().Bytes;
        }
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count < 2) return 0;
                var span = (_samples.Last().Ticks - _samples.Peek().Ticks) / (double)Stopwatch.Frequency;
                return span <= 0 ? 0 : _total / span;
            }
        }
    }
}


/// <summary>What the site says about the world right now. Null means we could
/// not ask, which is shown as a dash rather than a guess.</summary>
internal readonly record struct ServerStatus(bool? Online, int? Players)
{
    public static ServerStatus Unknown => new(null, null);

    public string ServerText => Online switch
    {
        true => "ONLINE",
        false => "OFFLINE",
        null => "—",
    };

    public string Players_ => Players?.ToString(CultureInfo.InvariantCulture) ?? "—";
}
