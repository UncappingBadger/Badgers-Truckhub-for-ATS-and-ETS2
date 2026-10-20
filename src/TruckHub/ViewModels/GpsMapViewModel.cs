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
    // Tunable; picked conservatively so ordinary curve slop doesn't trigger spurious reroutes. What
    // happens PAST it depends on the user's auto-reroute setting - see OffRouteMonitor.
    private const double OffRouteThresholdMeters = OffRouteMonitor.RecomputeThresholdMeters;

    // The off-route decision rules (auto-recompute at 200 m, or - the default - a manual alert at one mile; both are
    // engine metres, i.e. actual driving - see OffRouteMonitor's UNITS note). Reading the setting costs a file load, so it is cached briefly rather than read per tick.
    private readonly OffRouteMonitor _offRoute = new();

    // The truck's own measure of displayed distance per engine metre on the road it is on right now (the game's
    // time acceleration: 3 in towns .. 15-20 on highways); scales the next-turn distance. See DisplayScaleEstimator.
    private readonly DisplayScaleEstimator _displayScale = new();
    private SimGame _displayScaleGame = SimGame.Unknown;
    private readonly Func<bool> _getAutoReroute;
    private bool _autoRerouteCached;
    private DateTime _autoRerouteReadUtc = DateTime.MinValue;
    private static readonly TimeSpan AutoRerouteCacheTtl = TimeSpan.FromSeconds(5);

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

    // A route attempt can legitimately fail with the job destination unchanged - e.g. the truck
    // starts on the opposite side of a ferry-only crossing (see RoutingService/build-route-graph.mjs:
    // ferries are deliberately not routable edges) from where the destination sits. That's meant to
    // recover once the truck's live position ends up on the same landmass as the destination (i.e.
    // after actually taking the ferry) - found live (2026-09-06, a Newcastle-upon-Tyne -> Dijon job)
    // that it never did, because UpdateJobRoute only ever re-attempts on an actual destination
    // *change*. Retried periodically instead, whenever a destination is active but nothing's
    // currently showing - throttled well below the 1Hz position push so a long crossing doesn't
    // re-run A* over the whole graph every tick.
    private DateTime _lastRouteAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan RouteRetryInterval = TimeSpan.FromSeconds(15);

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

    /// <summary>Fires with the next detected turn ahead on the current route (direction + an
    /// already-unit-formatted distance string in the distances the game itself shows - see
    /// TurnDistanceFormatter), or null when there's no route or no turn within range. Both games.</summary>
    public event Action<(bool IsRight, string DistanceDisplay)?>? NextTurnUpdated;

    /// <summary>Fires when the OFF ROUTE alert turns on (true) or off (false) - the map shows a red
    /// warning and a Reroute button while it's on. Manual (default) mode only; see OffRouteMonitor.</summary>
    public event Action<bool>? OffRouteChanged;

    /// <summary>Current alert state, so a map window that opens (or reloads) mid-alert can show it
    /// immediately instead of waiting for the next change.</summary>
    public bool IsOffRoute => _offRoute.IsOffRoute;

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

    private readonly Func<bool> _getUseMetric;

    public GpsMapViewModel(TelemetryService telemetryService, Func<bool> getUseMetric, Func<bool> getAutoReroute)
    {
        _telemetryService = telemetryService;
        _getUseMetric = getUseMetric;
        _getAutoReroute = getAutoReroute;
        _telemetryService.SnapshotUpdated += OnSnapshotUpdated;
    }

    /// <summary>The user's "recompute automatically" choice, re-read at most every few seconds so a change
    /// in Settings takes effect promptly without a file load on every position tick.</summary>
    private bool ReadAutoReroute()
    {
        var now = DateTime.UtcNow;
        if (now - _autoRerouteReadUtc > AutoRerouteCacheTtl)
        {
            _autoRerouteReadUtc = now;
            try
            {
                _autoRerouteCached = _getAutoReroute();
            }
            catch
            {
                // Keep the last known value - a settings read failing must never break route tracking.
            }
        }

        return _autoRerouteCached;
    }

    /// <summary>Raises <see cref="OffRouteChanged"/> only if the alert was actually showing.</summary>
    private void ResetOffRouteAlert()
    {
        if (_offRoute.Reset())
        {
            OffRouteChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// The user asked for a new route (the map's Reroute button, or the Ctrl+Alt+F8 hotkey): recompute from
    /// the truck's current position to the same destination. Works whether or not the OFF ROUTE alert is
    /// showing. Does nothing - harmlessly - if there is no destination or no position yet.
    /// </summary>
    public void RerouteNow()
    {
        if (_currentDestinationResolver == null || !_hasKnownPosition)
        {
            AppLogger.Log("GpsMapViewModel: reroute requested but there is no active route/destination or live position - ignored.");
            return;
        }

        AppLogger.Log("GpsMapViewModel: reroute requested by the user - recomputing from the current position.");
        RunRouteComputation(_lastKnownGameX, _lastKnownGameZ, _currentDestinationResolver);
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

        if (_displayScaleGame != snapshot.Game)
        {
            _displayScale.Reset(); // a different game (or a fresh connection) starts the road-scale estimate over
            _displayScaleGame = snapshot.Game;
        }

        _displayScale.Update(snapshot.PositionX, snapshot.PositionZ, snapshot.OdometerKm);
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
            ResetOffRouteAlert(); // nothing to be off of
            NextTurnUpdated?.Invoke(null);
            return; // no active route to trim/check.
        }

        // Distance to the nearest point on the route POLYLINE (clamped projection onto each
        // segment), not just the nearest route VERTEX - a long dead-straight highway edge (see
        // build-route-graph.mjs: a road with zero real curvature collapses to a single edge, just
        // its two endpoint nodes, no interior points) can easily be several hundred meters long, so
        // the truck sitting exactly mid-segment on real road was up to half that edge's length from
        // the nearest vertex - well past OffRouteThresholdMeters even though it never left the route.
        // Confirmed live (2026-09-26): a Waterloo->Denver job's plains-state highway stretches
        // produced repeated ~200-215m (up to 892m) "off route" false positives every 15-90s for the
        // entire early part of the drive, each one triggering a full graph recompute - exactly the
        // input/display lag reported that session.
        var nearestIndex = 0;
        var nearestDistSq = double.MaxValue;
        for (var i = 0; i < route.Count - 1; i++)
        {
            var ax = route[i].X;
            var az = route[i].Z;
            var abx = route[i + 1].X - ax;
            var abz = route[i + 1].Z - az;
            var lenSq = abx * abx + abz * abz;
            var t = lenSq > 0 ? ((x - ax) * abx + (z - az) * abz) / lenSq : 0.0;
            t = Math.Clamp(t, 0.0, 1.0);
            var dx = ax + t * abx - x;
            var dz = az + t * abz - z;
            var distSq = dx * dx + dz * dz;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestIndex = i;
            }
        }

        var distanceToRoute = Math.Sqrt(nearestDistSq);

        // While a (re)computation is already running, the route being measured against is about to be
        // replaced - judging "off route" against it would just re-raise the alert the user dismissed by
        // asking for the reroute.
        if (distanceToRoute > OffRouteThresholdMeters && _routeComputationInFlight)
        {
            return;
        }

        var autoReroute = ReadAutoReroute();
        var action = _offRoute.Evaluate(distanceToRoute, autoReroute, out var alertChanged);
        if (alertChanged)
        {
            AppLogger.Log(_offRoute.IsOffRoute
                ? $"GpsMapViewModel: {distanceToRoute:F0}m of game world (one mile of real driving = {OffRouteMonitor.AlertThresholdMeters:F0}m; about {distanceToRoute * (_displayScale.Scale ?? TurnDistanceFormatter.DisplayedPerEngineUnit(_activeGame)) / 1609.344:F0} odometer miles at this road's scale) from the route - OFF ROUTE alert shown, waiting for the user to ask for a reroute."
                : "GpsMapViewModel: back on the route - OFF ROUTE alert cleared.");
            OffRouteChanged?.Invoke(_offRoute.IsOffRoute);
        }

        if (distanceToRoute > OffRouteThresholdMeters)
        {
            if (action == OffRouteAction.RecomputeNow && _currentDestinationResolver != null)
            {
                AppLogger.Log($"GpsMapViewModel: {distanceToRoute:F0}m off route - recomputing.");
                RunRouteComputation(x, z, _currentDestinationResolver);
            }
            else if (!autoReroute)
            {
                // Manual mode and not on the route: the next-turn arrow belongs to the route the truck is
                // no longer on, so hide it rather than point somewhere misleading. The route line itself
                // stays exactly as it was - pulling in for fuel or a rest area must not rebuild it.
                NextTurnUpdated?.Invoke(null);
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

        // Both games. ATS was confirmed on real driving first; ETS2 uses the same detector (the SDK's world
        // axes are identical) and the same sign, in the European blue the map's national-road plates use.
        // TurnDetector measures in engine metres - the formatter scales that up to the distances the game shows.
        if (!GameMapProfile.IsSupported(_activeGame))
        {
            NextTurnUpdated?.Invoke(null);
            return;
        }

        var turn = TurnDetector.FindNextTurn(_remainingRoutePoints);
        NextTurnUpdated?.Invoke(turn == null
            ? null
            : (turn.Value.IsRight, TurnDistanceFormatter.Format(turn.Value.DistanceMeters, _activeGame, _getUseMetric(), _displayScale.Scale)));
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
        ResetOffRouteAlert();
        RouteUpdated?.Invoke(Array.Empty<(double, double)>());
        NextTurnUpdated?.Invoke(null);
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
            // Same job, same destination - normally nothing to do. The exception: a destination
            // is active but nothing's currently showing, meaning the last attempt at it failed
            // (most likely a ferry-only crossing the truck hadn't made yet). Worth trying again
            // now that the truck may have moved, on a cooldown rather than every tick.
            if (hasDestination && _remainingRoutePoints.Count == 0 && !_routeComputationInFlight
                && DateTime.UtcNow - _lastRouteAttemptUtc > RouteRetryInterval)
            {
                RunRouteComputation(snapshot.PositionX, snapshot.PositionZ,
                    ResolveJobDestination(snapshot.CityDestinationId, snapshot.CompanyDestinationId));
            }
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

        _lastRouteAttemptUtc = DateTime.UtcNow;

        // Captured regardless of which caller (job destination, clearing a pin back to the job, a
        // manual pin, or AdvanceRoute's own deviation recompute) triggered this - keeps whatever
        // destination is "current" for AdvanceRoute to retarget on a future deviation, without every
        // caller needing to remember to set it themselves.
        _currentDestinationResolver = resolveDestination;
        _routeComputationInFlight = true;
        ResetOffRouteAlert(); // a new route is on its way from the truck's current position
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
