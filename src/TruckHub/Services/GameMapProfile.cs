using System;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Single switch for the GPS map's ATS/ETS2 dynamic-detection support - same idea as the main
/// window's existing D/N/P/R and unit-display handling, which already reads TelemetrySnapshot.Game
/// to adjust its own behavior per game. The GPS map's data (tiles, routing graph, projection) is
/// far more expensive to get right than a UI label, though, so this exists as its own explicit gate
/// rather than just checking snapshot.Game inline everywhere.
///
/// ETS2's map/routing data has been extracted and embedded (see Assets\WebMap\ets2-mapdata\,
/// Assets\WebMap\ets2-tiles.zip, Assets\RouteGraph\ets2-route-graph.zip, and
/// Assets\WebMap\GENERATION-Ets2.md for exactly how) and the C# service layer below can already
/// load/project it - but Ets2Enabled stays false until a real ETS2 session has actually confirmed
/// the projection lands truck positions in the right place and the routing graph produces sane
/// paths, the same live-verification every ATS fix in this feature went through before shipping.
/// Flip it to true only after that, not just because the data exists.
/// </summary>
public static class GameMapProfile
{
    /// <summary>The one line to change once ETS2 has been live-verified. Everything else in this
    /// class, and every caller that checks IsSupported, reacts to this automatically.</summary>
    public const bool Ets2Enabled = true;

    public static bool IsSupported(SimGame game) => game switch
    {
        SimGame.Ats => true,
        SimGame.Ets2 => Ets2Enabled,
        _ => false,
    };

    public static (double Lon, double Lat) ToLonLat(SimGame game, double gameX, double gameZ) =>
        game == SimGame.Ets2
            ? Ets2ProjectionService.ToLonLat(gameX, gameZ)
            : GpsProjectionService.ToLonLat(gameX, gameZ);

    public static (double GameX, double GameZ) ToGameCoords(SimGame game, double lon, double lat) =>
        game == SimGame.Ets2
            ? Ets2ProjectionService.ToGameCoords(lon, lat)
            : GpsProjectionService.ToGameCoords(lon, lat);

    // Heading->bearing math isn't game-specific - it's derived from the shared SCS SDK's own
    // Rotate() matrix (see GpsProjectionService.HeadingToBearingDegrees), not from either game's
    // own coordinate system, so both games use the exact same formula.
    public static double HeadingToBearingDegrees(float headingUnit) =>
        GpsProjectionService.HeadingToBearingDegrees(headingUnit);

    public static RoutingGraph LoadRoutingGraph(SimGame game) => RoutingGraph.Load(game);
}
