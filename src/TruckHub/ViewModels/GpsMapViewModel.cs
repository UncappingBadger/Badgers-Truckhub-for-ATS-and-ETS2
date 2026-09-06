using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TruckHub.Models;
using TruckHub.Services;

namespace TruckHub.ViewModels;

/// <summary>
/// Feeds live position updates - and a computed route, either from an active job's destination or
/// a manually-placed map pin - to the GPS map window. Deliberately its own, separate view model
/// (not MainViewModel) - it depends on nothing but TelemetryService's existing public event, so
/// opening/closing the map window can never affect the main HUD's own bindings, gauge-animation
/// timer, or CoreBalancer. Throttled independently of the main 4Hz telemetry poll - the map only
/// needs roughly one position update a second, not four.
/// </summary>
public sealed class GpsMapViewModel : IDisposable
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromSeconds(1);

    // How far off the currently-displayed route the truck can be before it's treated as a real
    // deviation (missed turn, deliberate detour) rather than just normal route-following
    // imprecision (interchange curve approximations, fallback edges, lane width) - see AdvanceRoute.
    // Tunable; picked conservatively so ordinary curve slop doesn't trigger spurious reroutes.
    private const double OffRouteThresholdMeters = 200.0;

    private readonly TelemetryService _telemetryService;
    private DateTime _lastPushUtc = DateTime.MinValue;
    private double _lastKnownGameX;
    private double _lastKnownGameZ;
    private bool _hasKnownPosition;

    // Whichever game the most recent supported snapshot came from - GameMapProfile.IsSupported
    // means this is always Ats today (Ets2Enabled is false), but everything below already keys off
    // this rather than assuming Ats, so flipping that one flag is the only change flipping the
    // switch needs on the C# side.
    private SimGame _activeGame = SimGame.Ats;

    // Keyed by which game it was loaded for - a session could in principle see a game change
    // mid-run (a different simulator launched), so a stale graph from the wrong game is never
    // reused silently.
    private RoutingGraph? _routingGraph;
    private SimGame _routingGraphGame;

    // Tracks only the job's own destination, independent of whatever route is currently displayed -
    // a manual pin (SetManualDestination) can override the display without disturbing this, so a
    // still-active, unchanged job doesn't silently re-fire and clobber a manual pin the user just
    // placed. A real job change (including one clearing to no job at all) always takes priority.
    private string? _lastJobDestinationKey;
    private bool _routeComputationInFlight;
    private bool _hasLoggedFirstSnapshot;

    // The currently-displayed route, kept in raw game (X, Z) alongside the projected (Lon, Lat) -
    // AdvanceRoute needs real-meter distances for the off-route check, and reusing the same raw
    // coordinates RoutingService/the graph already work in avoids any lon/lat-degree distance skew
    // (a degree of longitude isn't the same distance as a degree of latitude, especially across the
    // US map's latitude range) that comparing projected coordinates directly would introduce.
    // Replaced wholesale (never mutated in place) as the truck advances - see AdvanceRoute, which is
    // *why* the route visibly stops showing where the truck's already been, not just a display trick
    // layered on top - specifically so this stays a safe atomic reference swap between AdvanceRoute
    // (runs on whatever thread TelemetryService's SnapshotUpdated fires on) and ComputeRoute (runs
    // on its own Task.Run background thread), the same "no lock needed" reasoning LastPosition/
    // LastRoutePoints already rely on, without risking a torn read/write on an in-place mutation two
    // different threads could hit at once.
    private IReadOnlyList<(double X, double Z, double Lon, double Lat)> _remainingRoutePoints = Array.Empty<(double, double, double, double)>();

    // Whatever RunRouteComputation was last asked to resolve, kept so AdvanceRoute can retarget the
    // same destination on a detected deviation without needing new input - captured centrally in
    // RunRouteComputation itself so every caller (job destination, clearing a pin back to the job,
    // a manual pin, and this deviation recompute itself) keeps it current for free.
    private Func<RoutingGraph, int?>? _currentDestinationResolver;

    public event Action<GpsLivePosition>? LivePositionUpdated;

    /// <summary>Fires with the route's (lon, lat) points once computed, or an empty list to clear
    /// whatever was being shown.</summary>
    public event Action<IReadOnlyList<(double Lon, double Lat)>>? RouteUpdated;

    /// <summary>Latest known position/route, kept alongside the events above - GpsLanServer polls
    /// these directly (it has no window/event-subscriber of its own to push into) rather than
    /// needing its own separate subscription. Reference-assignment is atomic in .NET, so plain
    /// properties are enough here without extra locking - RunRouteComputation already guards
    /// against overlapping writers, and a reader briefly seeing the previous value mid-update is
    /// harmless for a "what's the latest state" poll.</summary>
    public GpsLivePosition? LastPosition { get; private set; }

    public IReadOnlyList<(double Lon, double Lat)> LastRoutePoints { get; private set; } = Array.Empty<(double, double)>();

    /// <summary>Which game's map data the page should load - read fresh off the telemetry
    /// service's own latest snapshot rather than this class's own _activeGame (which only updates
    /// once a snapshot has actually been processed, so it'd default to Ats for the entire span
    /// between this view model's construction and the first real snapshot). Consumed once, at
    /// GpsMapWindow's Navigate() call and GpsCoordinator's LAN URL - see gpsmap.js's own
    /// comment on why this can't be pushed over postMessage after the fact instead.</summary>
    public SimGame CurrentGame => _telemetryService.LastSnapshot.Game;

    public GpsMapViewModel(TelemetryService telemetryService)
    {
        _telemetryService = telemetryService;
        _telemetryService.SnapshotUpdated += OnSnapshotUpdated;
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        // One-shot diagnostic: investigating a report that an already-active job (in progress
        // before the GPS window/app connected) isn't picked up until the save is reloaded. Logs
        // the raw job fields on the very first snapshot this instance ever sees, regardless of the
        // throttle/SdkActive gates below, to see exactly what's actually arriving.
        if (!_hasLoggedFirstSnapshot)
        {
            _hasLoggedFirstSnapshot = true;
            AppLogger.Log($"GpsMapViewModel: first snapshot - SdkActive={snapshot.SdkActive}, Game={snapshot.Game}, "
                + $"OnJob={snapshot.OnJob}, CityDestinationId='{snapshot.CityDestinationId}', CompanyDestinationId='{snapshot.CompanyDestinationId}', "
                + $"CityDestination='{snapshot.CityDestination}', CompanyDestination='{snapshot.CompanyDestination}'");
        }

        // Ats always supported; Ets2 only once GameMapProfile.Ets2Enabled is deliberately flipped on
        // after live verification - see its own comment for why. A game this map data was never
        // generated for would project positions onto nonsense coordinates.
        if (!snapshot.SdkActive || !GameMapProfile.IsSupported(snapshot.Game))
        {
            return;
        }

        _activeGame = snapshot.Game;

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastPushUtc < PushInterval)
        {
            return;
        }

        _lastPushUtc = nowUtc;
        _lastKnownGameX = snapshot.PositionX;
        _lastKnownGameZ = snapshot.PositionZ;
        _hasKnownPosition = true;

        var (lon, lat) = GameMapProfile.ToLonLat(_activeGame, snapshot.PositionX, snapshot.PositionZ);
        var bearing = GameMapProfile.HeadingToBearingDegrees(snapshot.HeadingUnit);
        var position = new GpsLivePosition(lon, lat, bearing, snapshot.SpeedKph);
        LastPosition = position;
        LivePositionUpdated?.Invoke(position);

        UpdateJobRoute(snapshot);
        AdvanceRoute(snapshot.PositionX, snapshot.PositionZ);
    }

    /// <summary>Called every position tick while a route is active: trims off whatever's already
    /// been driven (the route should only ever show what's ahead, not behind), and if the truck
    /// has drifted meaningfully off the route entirely - a missed turn, a deliberate detour -
    /// recomputes from here to the same destination rather than leaving a route showing that no
    /// longer reflects where the truck is actually going.</summary>
    private void AdvanceRoute(double x, double z)
    {
        var route = _remainingRoutePoints; // stable local snapshot of the current reference
        if (route.Count < 2)
        {
            return; // no active route to trim/check.
        }

        var nearestIndex = 0;
        var nearestDistSq = double.MaxValue;
        for (var i = 0; i < route.Count; i++)
        {
            var dx = route[i].X - x;
            var dz = route[i].Z - z;
            var distSq = dx * dx + dz * dz;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestIndex = i;
            }
        }

        if (Math.Sqrt(nearestDistSq) > OffRouteThresholdMeters)
        {
            if (_currentDestinationResolver != null)
            {
                AppLogger.Log($"GpsMapViewModel: {Math.Sqrt(nearestDistSq):F0}m off route - recomputing.");
                RunRouteComputation(x, z, _currentDestinationResolver);
            }
            return;
        }

        if (nearestIndex > 0)
        {
            var trimmed = route.Skip(nearestIndex).ToList();
            _remainingRoutePoints = trimmed;
            // Deliberately doesn't fire RouteUpdated here - it used to, and pushing the entire
            // remaining route (up to ~3,500 points) through the WebView2 message bridge and having
            // MapLibre fully re-tessellate that GeoJSON LineString on nearly every ~1s tick got
            // visibly sluggish over a whole drive. gpsmap.js already gets a live position push every
            // tick regardless (a handful of numbers) and now trims its own local copy of the last
            // real route using that, so state here just needs to stay correct for whoever polls
            // LastRoutePoints later (GpsLanServer's /api/route, or this window reopening mid-trip),
            // not push anything itself.
            UpdateLastRoutePoints(trimmed);
        }
    }

    private void UpdateLastRoutePoints(IReadOnlyList<(double X, double Z, double Lon, double Lat)> route)
    {
        var points = new List<(double Lon, double Lat)>(route.Count);
        foreach (var p in route)
        {
            points.Add((p.Lon, p.Lat));
        }
        LastRoutePoints = points;
    }

    private void ClearRoute()
    {
        _remainingRoutePoints = Array.Empty<(double, double, double, double)>();
        _currentDestinationResolver = null;
        LastRoutePoints = Array.Empty<(double, double)>();
        RouteUpdated?.Invoke(Array.Empty<(double, double)>());
    }

    private void UpdateJobRoute(TelemetrySnapshot snapshot)
    {
        var hasDestination = snapshot.OnJob
            && !string.IsNullOrEmpty(snapshot.CityDestinationId)
            && !string.IsNullOrEmpty(snapshot.CompanyDestinationId);

        var destinationKey = hasDestination
            ? $"{snapshot.CityDestinationId}|{snapshot.CompanyDestinationId}"
            : null;

        if (destinationKey == _lastJobDestinationKey)
        {
            return; // job hasn't changed - leave whatever's currently displayed (job route or a
                     // manual pin override) alone.
        }

        AppLogger.Log($"GpsMapViewModel: job destination changed '{_lastJobDestinationKey ?? "(none)"}' -> '{destinationKey ?? "(none)"}' (OnJob={snapshot.OnJob})");
        _lastJobDestinationKey = destinationKey;

        if (destinationKey == null)
        {
            ClearRoute();
            return;
        }

        RunRouteComputation(snapshot.PositionX, snapshot.PositionZ,
            ResolveJobDestination(snapshot.CityDestinationId, snapshot.CompanyDestinationId));
    }

    private static Func<RoutingGraph, int?> ResolveJobDestination(string cityId, string companyId)
    {
        return graph =>
        {
            var destinationNode = RoutingService.ResolveCompanyNode(graph, cityId, companyId);
            if (destinationNode == null)
            {
                AppLogger.Log($"GpsMapViewModel: no route graph entry for destination {cityId}/{companyId}");
            }
            return destinationNode;
        };
    }

    /// <summary>Removes a manually-placed pin's override and falls back to whatever route would
    /// otherwise be showing - the current job's route if one's active, or nothing at all.</summary>
    public void ClearManualDestination()
    {
        if (_lastJobDestinationKey == null)
        {
            ClearRoute();
            return;
        }

        var parts = _lastJobDestinationKey.Split('|', 2);
        RunRouteComputation(_lastKnownGameX, _lastKnownGameZ, ResolveJobDestination(parts[0], parts[1]));
    }

    /// <summary>Routes from the truck's most recently known live position to a point the user
    /// clicked directly on the map, overriding whatever route (job-derived or an earlier manual
    /// pin) was showing - same one-shot, backgrounded computation as a job destination.</summary>
    public void SetManualDestination(double lon, double lat)
    {
        if (!_hasKnownPosition)
        {
            AppLogger.Log("GpsMapViewModel: ignoring manual pin - no live position yet.");
            return;
        }

        var (destX, destZ) = GameMapProfile.ToGameCoords(_activeGame, lon, lat);
        var startX = _lastKnownGameX;
        var startZ = _lastKnownGameZ;

        RunRouteComputation(startX, startZ, graph =>
            RoutingService.FindNearestNode(graph, (float)destX, (float)destZ));
    }

    private void RunRouteComputation(double startX, double startZ, Func<RoutingGraph, int?> resolveDestination)
    {
        if (_routeComputationInFlight)
        {
            return; // a newer request arrived mid-computation - whichever triggers this again later wins.
        }

        // Captured regardless of which caller (job destination, clearing a pin back to the job, a
        // manual pin, or AdvanceRoute's own deviation recompute) triggered this - keeps whatever
        // destination is "current" for AdvanceRoute to retarget on a future deviation, without every
        // caller needing to remember to set it themselves.
        _currentDestinationResolver = resolveDestination;
        _routeComputationInFlight = true;
        var game = _activeGame;
        Task.Run(() => ComputeRoute(startX, startZ, resolveDestination, game));
    }

    /// <summary>Kicks off loading the routing graph in the background as soon as this view model
    /// exists (window opened / LAN started) instead of waiting for the first real route request -
    /// most sessions that open the GPS map already have an active job, so ComputeRoute would need
    /// this within a second or two anyway. Harmless if it races with ComputeRoute's own lazy load
    /// below - both just assign the same result to _routingGraph, worst case is one redundant load
    /// racing itself, never a crash (reference assignment, no torn state).</summary>
    public void PrewarmRoutingGraph()
    {
        var game = _activeGame;
        if (_routingGraph == null || _routingGraphGame != game)
        {
            Task.Run(() => LoadRoutingGraphIfNeeded(game));
        }
    }

    private void LoadRoutingGraphIfNeeded(SimGame game)
    {
        if (_routingGraph != null && _routingGraphGame == game)
        {
            return;
        }

        _routingGraph = GameMapProfile.LoadRoutingGraph(game);
        _routingGraphGame = game;
    }

    private void ComputeRoute(double startX, double startZ, Func<RoutingGraph, int?> resolveDestination, SimGame game)
    {
        try
        {
            // Already running on a background thread - no need to hop to yet another one just to
            // load the graph.
            LoadRoutingGraphIfNeeded(game);
            var graph = _routingGraph!;

            var destinationNode = resolveDestination(graph);
            if (destinationNode == null)
            {
                AppLogger.Log("GpsMapViewModel: destination did not resolve to a graph node - clearing route.");
                ClearRoute();
                return;
            }

            var startNode = RoutingService.FindNearestNode(graph, (float)startX, (float)startZ);
            var path = RoutingService.FindRoute(graph, startNode, destinationNode.Value);
            if (path == null)
            {
                AppLogger.Log("GpsMapViewModel: no route found to the requested destination - clearing route.");
                ClearRoute();
                return;
            }

            var richPoints = new List<(double X, double Z, double Lon, double Lat)>(path.Count);
            foreach (var nodeIndex in path)
            {
                var (lon, lat) = GameMapProfile.ToLonLat(game, graph.NodeX[nodeIndex], graph.NodeZ[nodeIndex]);
                richPoints.Add((graph.NodeX[nodeIndex], graph.NodeZ[nodeIndex], lon, lat));
            }

            AppLogger.Log($"GpsMapViewModel: route computed - {richPoints.Count} points.");
            _remainingRoutePoints = richPoints;
            UpdateLastRoutePoints(richPoints);
            // A genuine new route (unlike a trim tick, see AdvanceRoute) - this is the rare case
            // that's actually worth pushing the full route through the message bridge for.
            RouteUpdated?.Invoke(LastRoutePoints);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"GpsMapViewModel: route computation failed: {ex}");
        }
        finally
        {
            _routeComputationInFlight = false;
        }
    }

    public void Dispose()
    {
        _telemetryService.SnapshotUpdated -= OnSnapshotUpdated;
    }
}
