using System;
using System.Collections.Generic;

namespace TruckHub.Services;

/// <summary>
/// Finds the next real turn ahead on the currently-drawn route, from raw route-point geometry
/// alone - no per-edge heading data needed from the route graph (RoutingGraph.cs parses one but
/// never stores it; reusing that would mean touching the binary format for no real benefit here).
/// Works entirely in the same raw game (X, Z) coordinates AdvanceRoute already uses for its own
/// off-route distance check, for the same reason: lon/lat degrees aren't equal-distance, so heading
/// and distance math on projected coordinates would be skewed.
/// </summary>
public static class TurnDetector
{
    // Local heading at a point is the direction from that point to another point roughly this far
    // ahead along the route - short enough to react to a real turn quickly, long enough that the
    // many closely-spaced synthetic curve-interpolation points a route can pass through (see
    // build-route-graph.mjs's road-curve/prefab-curve chaining) don't each register as their own
    // tiny "turn".
    private const double HeadingWindowMeters = 35.0;

    // How far the local heading has to diverge from the truck's current heading before it counts
    // as a real turn rather than a highway's own gentle sweep - a sweeping curve changes heading
    // gradually over a long distance (low degrees per meter); an actual turn at a junction changes
    // it sharply over a short one. This threshold is what tells those apart, not the shape of the
    // road itself.
    private const double TurnThresholdDegrees = 28.0;

    // Don't report a turn further out than this - a real GPS unit only ever announces the next
    // maneuver, not everything for the rest of the trip.
    private const double MaxSearchMeters = 3000.0;

    public readonly record struct TurnInfo(bool IsRight, double DistanceMeters);

    /// <summary>Route must already be trimmed to "what's ahead" (see AdvanceRoute) - point 0 is
    /// taken as approximately where the truck is now.</summary>
    public static TurnInfo? FindNextTurn(IReadOnlyList<(double X, double Z, double Lon, double Lat)> route)
    {
        if (route.Count < 2)
        {
            return null;
        }

        // Compares the heading of the ~20m *approaching* each candidate point against the heading
        // of the ~20m *leaving* it - a genuinely local rate-of-change check, not "how far has
        // heading drifted from where the truck started". That distinction matters: comparing
        // against a fixed reference (tried first, caught by testing before this ever shipped) means
        // ANY sustained curve eventually accumulates past the threshold no matter how gentle - a
        // real 1500m-radius freeway sweep falsely fired as a "turn" at 916m. A sharp junction turn
        // changes heading a lot within a short local window; a gentle highway curve changes it only
        // a degree or two per window no matter how far you walk along it - only the local
        // before/after comparison actually tells those apart.
        double cumulativeMeters = 0;
        for (var i = 1; i < route.Count - 1 && cumulativeMeters < MaxSearchMeters; i++)
        {
            cumulativeMeters += Distance(route[i - 1], route[i]);

            var headingBehind = HeadingOverWindow(route, i, forward: false);
            var headingAhead = HeadingOverWindow(route, i, forward: true);
            if (headingBehind == null || headingAhead == null)
            {
                continue;
            }

            var delta = NormalizeDegrees(headingAhead.Value - headingBehind.Value);
            if (Math.Abs(delta) >= TurnThresholdDegrees)
            {
                // Desk math said positive delta = right (see git history for the full derivation,
                // cross-checked two independent ways against GpsProjectionService's own axis signs
                // and heading-to-bearing conversion) - but live-confirmed backwards on a real ATS
                // right turn (2026-09-08), so flipped empirically rather than re-deriving further.
                // Something about the live data path doesn't match what the synthetic point tests
                // assumed - worth a real look later, but this is the actually-correct behavior now.
                return new TurnInfo(delta < 0, cumulativeMeters);
            }
        }

        return null;
    }

    /// <summary>Heading of the ~HeadingWindowMeters of route immediately behind (forward: false) or
    /// ahead of (forward: true) route[index] - a short local window, not a distance from either end
    /// of the whole route.</summary>
    private static double? HeadingOverWindow(IReadOnlyList<(double X, double Z, double Lon, double Lat)> route, int index, bool forward)
    {
        var pivot = route[index];
        var traveled = 0.0;
        var prev = pivot;
        var step = forward ? 1 : -1;
        for (var i = index + step; forward ? i < route.Count : i >= 0; i += step)
        {
            traveled += Distance(prev, route[i]);
            prev = route[i];
            if (traveled >= HeadingWindowMeters)
            {
                return forward
                    ? RadiansToDegrees(Math.Atan2(route[i].Z - pivot.Z, route[i].X - pivot.X))
                    : RadiansToDegrees(Math.Atan2(pivot.Z - route[i].Z, pivot.X - route[i].X));
            }
        }

        // Ran out of route before the window was covered - use whatever's left rather than nothing,
        // as long as there's at least some real distance to derive a direction from.
        if (traveled > 1.0)
        {
            return forward
                ? RadiansToDegrees(Math.Atan2(prev.Z - pivot.Z, prev.X - pivot.X))
                : RadiansToDegrees(Math.Atan2(pivot.Z - prev.Z, pivot.X - prev.X));
        }

        return null;
    }

    private static double Distance((double X, double Z, double Lon, double Lat) a, (double X, double Z, double Lon, double Lat) b)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static double NormalizeDegrees(double degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }
}
