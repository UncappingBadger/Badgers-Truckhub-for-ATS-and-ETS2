using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using TruckHub.Models;
using TruckHub.Services;
using TruckHub.ViewModels;

namespace TruckHub.Views;

/// <summary>
/// Live GPS map - WindowChrome-styled the same as LogWindow (see its own comment), hosting a
/// WebView2 page that renders the truck's real position over real ATS roads/cities/state
/// boundaries. The WebView2 control itself is only ever created here, in this window's Loaded
/// handler - but the CoreWebView2Environment it uses may already exist by then (see
/// GpsCoordinator.Prewarm), so the very first window of an app session can skip straight past the
/// slow part of opening it.
/// </summary>
public partial class GpsMapWindow : Window
{
    // Matches this window's own XAML-declared initial Width/Height - "reset to default size" just
    // restores these, it doesn't need to know anything about where MainWindow happened to place it.
    private const double DefaultWidth = 900;
    private const double DefaultHeight = 650;

    private readonly GpsCoordinator _coordinator;
    private readonly GpsMapViewModel _viewModel;
    private string _webView2UserDataFolder = string.Empty;
    private bool _webViewReady;

    // A route (and, in principle, a live position) can arrive before the page has actually finished
    // loading and registered its message listener - confirmed live: an already-active job's route
    // computes almost immediately on connect, often faster than WebView2 can navigate and the page
    // can run its own startup JS, so the very first PostWebMessageAsJson call was landing on a page
    // with nobody listening yet and getting silently dropped, no retry. The latest of each gets
    // cached here and replayed once the page actually confirms it's ready (see "mapReady" below) -
    // "latest wins" is enough since both are idempotent full-state pushes, not incremental deltas.
    private IReadOnlyList<(double Lon, double Lat)>? _pendingRoute;
    private GpsLivePosition? _pendingPosition;

    // WebView2's renderer/GPU subprocesses are separate OS processes Windows schedules on its own -
    // CoreBalancer steering TruckHub.exe's own affinity away from the busiest cores never touched
    // these at all. Registered once the environment exists (see InitializeWebViewAsync), released
    // on close so a later rebalance cycle doesn't try to touch a process handle that's gone.
    private readonly List<Process> _registeredWebViewProcesses = new();

    // CoreBalancer decides 30 vs 60 from real system-wide CPU headroom (the same per-core samples
    // it already takes for its own affinity steering) - kept as a named handler (not a lambda
    // passed straight to +=) specifically so Closed can unsubscribe the exact same delegate,
    // otherwise this window would keep getting pushed FPS changes indefinitely after closing.
    private readonly Action<int> _onFpsRecommendationChanged;

    public GpsMapWindow(GpsCoordinator coordinator, double left, double top)
    {
        InitializeComponent();

        _onFpsRecommendationChanged = PushFpsCap;
        CoreBalancer.FpsRecommendationChanged += _onFpsRecommendationChanged;

        _coordinator = coordinator;
        // The coordinator, not this window, owns the view model - LAN Mode (toggled from this
        // window, see the "toggleLan" message handling below) is explicitly meant to keep running
        // after this window closes, so the underlying position/route tracking can't be tied to
        // this window's own lifecycle. AcquireForWindow() returns the same shared instance LAN Mode
        // would also be using, creating it fresh only if this is the very first thing to need it.
        _viewModel = coordinator.AcquireForWindow();
        _viewModel.LivePositionUpdated += OnLivePositionUpdated;
        _viewModel.RouteUpdated += OnRouteUpdated;
        _viewModel.NextTurnUpdated += OnNextTurnUpdated;

        // If this window is reopening while the shared view model was kept alive by LAN Mode, its
        // events already fired for the "current" state before this window existed to hear them -
        // seed the same pending-replay path InitializeWebViewAsync's "mapReady" handler already
        // uses so the reopened page shows the real current state immediately, not just whatever
        // arrives on the next natural change (position updates every ~1s regardless, but the route
        // only fires on an actual destination change, which could otherwise mean no route line for
        // the rest of the trip).
        _pendingPosition = _viewModel.LastPosition;
        if (_viewModel.LastRoutePoints.Count > 0)
        {
            _pendingRoute = _viewModel.LastRoutePoints;
        }

        Left = left;
        Top = top;

        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closed += (_, _) =>
        {
            _viewModel.LivePositionUpdated -= OnLivePositionUpdated;
            _viewModel.RouteUpdated -= OnRouteUpdated;
            _viewModel.NextTurnUpdated -= OnNextTurnUpdated;
            CoreBalancer.FpsRecommendationChanged -= _onFpsRecommendationChanged;
            // The coordinator owns the view model now, not this window - it only actually disposes
            // it once nothing (this window, or LAN Mode) needs it anymore.
            _coordinator.ReleaseWindow();
            foreach (var process in _registeredWebViewProcesses)
            {
                CoreBalancer.UnregisterProcess(process);
            }
            MapWebView.Dispose();

            // Best-effort - the browser subprocess may still be releasing file handles right after
            // Dispose() returns, so a failed delete here just leaves a few MB behind occasionally,
            // not a real problem.
            try
            {
                if (Directory.Exists(_webView2UserDataFolder))
                {
                    Directory.Delete(_webView2UserDataFolder, recursive: true);
                }
            }
            catch
            {
                // Ignored - see comment above.
            }
        };
    }

    private async System.Threading.Tasks.Task InitializeWebViewAsync()
    {
        try
        {
            // Extraction (unpacking the ~92,000 pre-generated tile files to disk on first run) is
            // real disk I/O, and environment creation spins up a whole Chromium browser subprocess -
            // both real costs, kept off the UI thread here even though most sessions now find them
            // already done (or well underway) thanks to GpsCoordinator.Prewarm running them in the
            // background since app startup.
            var webMapDirTask = _coordinator.GetWebMapDirAsync();

            CoreWebView2Environment env;
            var prewarmed = _coordinator.ClaimPrewarmedEnvironment();
            if (prewarmed != null)
            {
                (env, _webView2UserDataFolder) = await prewarmed;
            }
            else
            {
                // No pre-warmed environment left to claim - either this is a closed-then-reopened
                // window (see the class comment: only the first window of a session gets one) or
                // Prewarm() genuinely hasn't finished yet. Either way, a fresh folder per instance,
                // not one shared/reused path - closing and quickly reopening this window reused the
                // same WebView2 user-data folder before, and the previous environment's browser
                // subprocess hadn't necessarily finished releasing it yet, which showed up as tile
                // fetches silently failing only on the second-or-later open. A unique folder per
                // instance makes that class of conflict structurally impossible.
                _webView2UserDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TruckHub-Dev", "WebView2", Guid.NewGuid().ToString("N"));
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: _webView2UserDataFolder);
            }

            var webMapDir = await webMapDirTask;
            await MapWebView.EnsureCoreWebView2Async(env);

            MapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "truckhub.app", webMapDir, CoreWebView2HostResourceAccessKind.Allow);

            // WebView2's built-in virtual-host file server doesn't recognize the .mjs extension and
            // serves it without a usable Content-Type, which a strict ES module import rejects
            // outright ("Failed to fetch dynamically imported module"). Vendored MapLibre ships as
            // .mjs, so intercept just those requests and serve them ourselves with the right header;
            // everything else (html/css/json/pbf) is left to the default handler, which serves the
            // disk-extracted tiles just fine.
            MapWebView.CoreWebView2.AddWebResourceRequestedFilter(
                "https://truckhub.app/*.mjs", CoreWebView2WebResourceContext.All);
            MapWebView.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                if (!args.Request.Uri.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var relativePath = new Uri(args.Request.Uri).AbsolutePath.TrimStart('/');
                var filePath = Path.Combine(webMapDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(filePath))
                {
                    return;
                }

                var content = File.OpenRead(filePath);
                args.Response = env.CreateWebResourceResponse(
                    content, 200, "OK", "Content-Type: text/javascript");
            };

            // The page forwards its own console/module errors here (see gpsmap.js's reportToHost) -
            // WebView2 has no console we can see from outside the running app, so this is the only
            // way to diagnose a rendering problem without the user opening DevTools themselves.
            MapWebView.CoreWebView2.WebMessageReceived += async (_, args) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                    var root = doc.RootElement;
                    var messageType = root.GetProperty("type").GetString();
                    if (messageType == "jsLog")
                    {
                        var level = root.GetProperty("level").GetString();
                        var message = root.GetProperty("message").GetString();
                        AppLogger.Log($"GpsMapWindow (js {level}): {message}");
                    }
                    else if (messageType == "mapReady")
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;

                        // The page itself (its map object and message listener) is only actually
                        // ready now - not right after Navigate() returns, which is when this used to
                        // be set. An already-active job's route computes almost immediately on
                        // connect, often faster than the page finishes loading, so the very first
                        // push was landing before anyone was listening and getting silently dropped
                        // with no retry - that's what "job already active at launch isn't detected
                        // until the save is reloaded" actually was. Replay whatever arrived early.
                        _webViewReady = true;
                        if (_pendingRoute != null)
                        {
                            PushRoute(_pendingRoute);
                            _pendingRoute = null;
                        }
                        if (_pendingPosition != null)
                        {
                            PushPosition(_pendingPosition);
                            _pendingPosition = null;
                        }
                        // If this window opened while LAN Mode was already active from an earlier
                        // session with this window, reflect that on the button/status text
                        // immediately rather than leaving the page looking like it's off.
                        PushLanStatus(_coordinator.GetLanUrlIfActive());

                        // Only registered here, not right after EnsureCoreWebView2Async - the GPU
                        // process specifically doesn't exist until the page's own JS actually
                        // initializes a WebGL context (MapLibre's `new Map(...)`), and "mapReady"
                        // is the first point that's guaranteed to have already happened.
                        RegisterWebViewProcessesWithCoreBalancer();

                        // The page's own default (30, see gpsmap.js) is already reasonable, but push
                        // whatever CoreBalancer has already established (e.g. it's had headroom to
                        // recommend 60 for a while before this particular window happened to open).
                        PushFpsCap(CoreBalancer.RecommendedFps);
                    }
                    else if (messageType == "setPinDestination")
                    {
                        var lon = root.GetProperty("lon").GetDouble();
                        var lat = root.GetProperty("lat").GetDouble();
                        _viewModel.SetManualDestination(lon, lat);
                    }
                    else if (messageType == "clearPin")
                    {
                        _viewModel.ClearManualDestination();
                    }
                    else if (messageType == "toggleLan")
                    {
                        if (_coordinator.IsLanActive)
                        {
                            _coordinator.StopLan();
                            PushLanStatus(null);
                        }
                        else
                        {
                            var useSsl = root.TryGetProperty("useSsl", out var useSslProp) && useSslProp.GetBoolean();
                            var url = await _coordinator.StartLanAsync(useSsl);
                            PushLanStatus(url);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"GpsMapWindow: failed to parse web message: {ex}");
                }
            };

            // Which game's tiles/mapdata folder the page should load - see gpsmap.js's own comment
            // on why this has to arrive as a URL param rather than a postMessage sent after the
            // page's already loaded. Read once, right now - if the game connects or changes after
            // this window's already open, the page keeps using whatever it started with until
            // reopened, same "not a live hot-swap" limitation GENERATION-Ets2.md already documents.
            var gameParam = _viewModel.CurrentGame == SimGame.Ets2 ? "ets2" : "ats";
            MapWebView.CoreWebView2.Navigate($"https://truckhub.app/index.html?game={gameParam}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"GpsMapWindow: WebView2 init failed: {ex}");
        }
    }

    private void OnLivePositionUpdated(GpsLivePosition position)
    {
        if (!_webViewReady)
        {
            // Page hasn't confirmed it's ready to receive messages yet - keep only the latest, and
            // let the "mapReady" handler above replay it once it actually is.
            _pendingPosition = position;
            return;
        }

        PushPosition(position);
    }

    private void OnRouteUpdated(IReadOnlyList<(double Lon, double Lat)> points)
    {
        if (!_webViewReady)
        {
            _pendingRoute = points;
            return;
        }

        PushRoute(points);
    }

    // No pending-replay needed here unlike position/route above - this recomputes on every
    // position tick regardless (see GpsMapViewModel.AdvanceRoute), not only on a destination
    // change, so a page that wasn't ready for the first one gets a correct value within ~1s anyway.
    private void OnNextTurnUpdated((bool IsRight, string DistanceDisplay)? turn)
    {
        if (!_webViewReady)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (MapWebView.CoreWebView2 == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new
            {
                type = "nextTurn",
                isRight = turn?.IsRight,
                distance = turn?.DistanceDisplay,
            });
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    private void PushPosition(GpsLivePosition position)
    {
        Dispatcher.Invoke(() =>
        {
            if (MapWebView.CoreWebView2 == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new
            {
                type = "livePosition",
                lon = position.Lon,
                lat = position.Lat,
                bearing = position.BearingDegrees,
                speedKph = position.SpeedKph,
            });
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    private void PushRoute(IReadOnlyList<(double Lon, double Lat)> points)
    {
        Dispatcher.Invoke(() =>
        {
            if (MapWebView.CoreWebView2 == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new
            {
                type = "route",
                points = points.Select(p => new[] { p.Lon, p.Lat }).ToArray(),
            });
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    private void PushLanStatus(string? url)
    {
        Dispatcher.Invoke(() =>
        {
            if (MapWebView.CoreWebView2 == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new
            {
                type = "lanStatus",
                active = url != null,
                url,
            });
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    /// <summary>Pushes CoreBalancer's current 30/60 recommendation to the page's
    /// requestAnimationFrame throttle (see gpsmap.js) - called once the map first signals ready,
    /// and again every time CoreBalancer's own recommendation actually changes.</summary>
    private void PushFpsCap(int targetFps)
    {
        Dispatcher.Invoke(() =>
        {
            if (MapWebView.CoreWebView2 == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new { type = "fpsCap", targetFps });
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    /// <summary>Hands this window's WebView2 renderer/GPU subprocesses to CoreBalancer so they get
    /// steered off the busiest cores the same way TruckHub.exe itself already is - these are
    /// entirely separate OS processes, so setting TruckHub's own affinity alone never touched them.
    /// Only Renderer/Gpu kinds are registered - the browser/utility/crashpad-handler processes
    /// aren't doing the actual per-frame map rendering work this is meant to help.</summary>
    private void RegisterWebViewProcessesWithCoreBalancer()
    {
        try
        {
            foreach (var info in MapWebView.CoreWebView2.Environment.GetProcessInfos())
            {
                if (info.Kind != CoreWebView2ProcessKind.Renderer && info.Kind != CoreWebView2ProcessKind.Gpu)
                {
                    continue;
                }

                var process = Process.GetProcessById(info.ProcessId);
                _registeredWebViewProcesses.Add(process);
                CoreBalancer.RegisterProcess(process);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"GpsMapWindow: failed to register WebView2 processes with CoreBalancer: {ex}");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ResetSize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        Width = DefaultWidth;
        Height = DefaultHeight;
    }
}
