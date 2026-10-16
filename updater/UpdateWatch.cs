using System.Windows;
using AdenRising.Updater.Core;
using WinForms = System.Windows.Forms;

namespace AdenRising.Updater;

/// <summary>
/// A launcher left running for days -- in the tray while somebody plays, or
/// just open on a computer that is never switched off -- used to learn about a
/// new client only when Play was pressed. Now it looks every quarter of an
/// hour. With no game up it simply updates, the same way a fresh start would;
/// with a game up the files are in use, so it says so once per release and
/// leaves the rest to the next Play.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan UpdateWatchInterval = TimeSpan.FromMinutes(15);

    private System.Windows.Threading.DispatcherTimer? _updateWatch;
    private bool _updateWatchBusy;
    private string? _announcedClient;

    private void StartUpdateWatch()
    {
        if (_updateWatch is not null) return;
        _updateWatch = new System.Windows.Threading.DispatcherTimer { Interval = UpdateWatchInterval };
        _updateWatch.Tick += async (_, _) => await UpdateWatchTickAsync();
        _updateWatch.Start();
    }

    private async Task UpdateWatchTickAsync()
    {
        // Only from rest: never in the middle of a download, an error screen or a launch.
        if (_updateWatchBusy || _playing || PlayButton.Visibility != Visibility.Visible) return;
        _updateWatchBusy = true;
        try
        {
            if (AnyGameRunning())
            {
                var offered = await OfferedClientAsync();
                if (offered is null || offered == _installer.InstalledVersion() || offered == _announcedClient) return;
                _announcedClient = offered;
                _tray?.ShowBalloonTip(10000, "Aden Rising update ready",
                    $"Version {offered} is out. Close the game and press Play to get it.", WinForms.ToolTipIcon.Info);
                return;
            }

            // The launcher first, as on a fresh start: a newer client may need a newer launcher to read it.
            if (await TrySelfUpdateAsync()) return;   // being replaced
            if (PlayButton.Visibility != Visibility.Visible) await CheckAsync();   // a launcher update that could not be applied

            if (await ClientOutdatedAsync())
            {
                var wasDown = WindowState == WindowState.Minimized || !IsVisible;
                await CheckAsync();
                var now = _installer.InstalledVersion();
                if (wasDown && now is not null && now == _manifest?.Version && _tray is not null)
                {
                    // The icon is only up while a game runs; it goes again when the balloon does (SetUpTray).
                    _tray.Visible = true;
                    _tray.ShowBalloonTip(8000, "Aden Rising updated",
                        $"The game is up to date (version {now}).", WinForms.ToolTipIcon.Info);
                }
            }
        }
        catch { /* an update server that cannot be reached is no reason to bother anybody */ }
        finally
        {
            _updateWatchBusy = false;
        }
    }

    /// <summary>The client version the server offers, or null when it cannot be asked.</summary>
    private async Task<string?> OfferedClientAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var json = await _http.GetStringAsync(new Uri(new Uri(BaseUrl), "manifests/latest.v2.json"), cts.Token);
            return Manifest.ParseSigned(json).Item1.Version;
        }
        catch
        {
            return null;
        }
    }
}
