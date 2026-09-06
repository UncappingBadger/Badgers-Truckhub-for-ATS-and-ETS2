using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using TruckHub.Models;
using TruckHub.ViewModels;

namespace TruckHub.Services;

/// <summary>
/// Owns the GPS feature's shared, potentially-long-lived state - the GpsMapViewModel (position/
/// route tracking) and the optional GpsLanServer - so LAN Mode can keep running after the map
/// window itself closes (explicit ask: toggled from GpsMapWindow, but once active the window can
/// be closed without stopping it). Lazily created on first real use (window opened OR LAN toggled
/// on) and torn down only once neither consumer needs it anymore.
///
/// Also owns pre-warming (see Prewarm()) - a deliberate departure from this class's original
/// "costs nothing until used" discipline, traded away on explicit request in favor of a much
/// faster first GPS-window open: live testing showed the main window going near-unresponsive for
/// close to a minute after first opening the map, almost entirely WebView2's own Chromium
/// subprocess cold-start cost. A session that never touches GPS at all now pays a small, constant
/// cost instead (one extra idle browser subprocess) - accepted trade, not an oversight.
///
/// Owned by MainWindow, one instance for the app's lifetime (unlike GpsMapWindow/GpsMapViewModel,
/// which come and go) - it's the thing that outlives the window, not the other way around.
/// </summary>
public sealed class GpsCoordinator
{
    private readonly TelemetryService _telemetryService;
    private GpsMapViewModel? _viewModel;
    private GpsLanServer? _lanServer;
    private bool _lanUseSsl;
    private bool _windowOpen;

    // Kicked off once by Prewarm() (called from MainWindow shortly after startup) so both of the
    // first GPS window's real one-time costs - unpacking the tiles to disk, and spinning up a
    // WebView2 browser subprocess - happen quietly in the background while the user is doing
    // anything else, instead of all landing at once the moment they actually click "GPS".
    private Task<string>? _webMapDirTask;
    private Task<(CoreWebView2Environment Env, string UserDataFolder)>? _prewarmedEnvironmentTask;

    public GpsCoordinator(TelemetryService telemetryService)
    {
        _telemetryService = telemetryService;
    }

    public bool IsLanActive => _lanServer != null;
    public int? LanPort => _lanServer?.Port;

    /// <summary>Starts the background pre-warm work - call once, early in the app's lifetime.
    /// Safe to call before the WPF message loop is pumping yet; both tasks are simply started and
    /// stored, not awaited here.</summary>
    public void Prewarm()
    {
        _webMapDirTask ??= Task.Run(WebMapAssetExtractor.EnsureExtracted);
        _prewarmedEnvironmentTask ??= CreateEnvironmentAsync();
    }

    private static async Task<(CoreWebView2Environment, string)> CreateEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TruckHub-Dev", "WebView2", Guid.NewGuid().ToString("N"));
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        return (env, userDataFolder);
    }

    /// <summary>Extraction is idempotent and cheap after the very first real run (a hash check
    /// against what's already on disk - see WebMapAssetExtractor), so every window/LAN start
    /// shares this one cached task instead of each doing its own redundant check. Falls back to
    /// starting it on the spot if Prewarm() was never called (e.g. a future caller that forgets
    /// to) rather than requiring callers to know about that ordering.</summary>
    public Task<string> GetWebMapDirAsync() => _webMapDirTask ??= Task.Run(WebMapAssetExtractor.EnsureExtracted);

    /// <summary>Hands over the pre-warmed environment/folder pair exactly once - only the very
    /// first GpsMapWindow to open in this app session gets to use it; returns null after that (or
    /// if Prewarm() was never called / hasn't started yet). A closed-then-reopened window still
    /// needs its own fresh environment tied to its own fresh folder - see GpsMapWindow's own
    /// comment on the real tile-fetch failures a shared/reused folder caused across reopens - so
    /// this deliberately isn't reusable across multiple claims.</summary>
    public Task<(CoreWebView2Environment Env, string UserDataFolder)>? ClaimPrewarmedEnvironment()
    {
        var task = _prewarmedEnvironmentTask;
        _prewarmedEnvironmentTask = null;
        return task;
    }

    /// <summary>Called when GpsMapWindow opens - returns the shared view model, creating it if
    /// this is the very first thing (window or LAN) to need it.</summary>
    public GpsMapViewModel AcquireForWindow()
    {
        _windowOpen = true;
        var viewModel = _viewModel ??= new GpsMapViewModel(_telemetryService);
        viewModel.PrewarmRoutingGraph();
        return viewModel;
    }

    /// <summary>Called when GpsMapWindow closes - does NOT tear down the shared state if LAN Mode
    /// is still active.</summary>
    public void ReleaseWindow()
    {
        _windowOpen = false;
        MaybeTearDown();
    }

    /// <summary>Starts LAN Mode (idempotent - returns the existing server's address, ignoring
    /// `useSsl`, if already running) and returns the LAN-facing URL to show the user, or null if no
    /// usable network address could be found. `useSsl` is the user's own per-launch choice from the
    /// LAN prompt in GpsMapWindow - Apple device users should pick plain HTTP (see GpsLanServer's own
    /// class comment for why), everyone else can opt into HTTPS if they'd rather.</summary>
    public async Task<string?> StartLanAsync(bool useSsl)
    {
        if (_lanServer == null)
        {
            var viewModel = _viewModel ??= new GpsMapViewModel(_telemetryService);
            viewModel.PrewarmRoutingGraph();
            var webMapDir = await GetWebMapDirAsync();
            _lanServer = new GpsLanServer(webMapDir, () => viewModel.LastPosition, () => viewModel.LastRoutePoints,
                () => viewModel.CurrentGame == SimGame.Ets2 ? "ets2" : "ats");
            _lanServer.Start(useSsl);
            _lanUseSsl = useSsl;
        }

        return BuildUrl();
    }

    /// <summary>The current LAN URL if a server is already running (e.g. to redisplay it when the
    /// map window reopens while LAN Mode was left active), or null if it isn't.</summary>
    public string? GetLanUrlIfActive() => _lanServer != null ? BuildUrl() : null;

    private string? BuildUrl()
    {
        var ip = GpsLanServer.FindLanIPv4Address();
        var scheme = _lanUseSsl ? "https" : "http";
        return ip != null && _lanServer != null ? $"{scheme}://{ip}:{_lanServer.Port}" : null;
    }

    public void StopLan()
    {
        _lanServer?.Stop();
        _lanServer = null;
        MaybeTearDown();
    }

    /// <summary>Stops everything unconditionally - called when the whole app is closing, not just
    /// the GPS window.</summary>
    public void Shutdown()
    {
        _lanServer?.Stop();
        _lanServer = null;
        _windowOpen = false;
        MaybeTearDown();
    }

    private void MaybeTearDown()
    {
        if (!_windowOpen && _lanServer == null && _viewModel != null)
        {
            _viewModel.Dispose();
            _viewModel = null;
        }
    }
}
