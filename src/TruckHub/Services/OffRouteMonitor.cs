namespace TruckHub.Services;

public enum OffRouteAction
{
    None,
    RecomputeNow,
}

/// <summary>
/// Decides what to do about the truck being away from the displayed GPS route. Pure logic (no map, no
/// timers, no settings I/O) so the rules are verified exactly as the app runs them; GpsMapViewModel feeds
/// it the truck's distance to the route every position tick.
///
/// UNITS: every distance here is in the game ENGINE's world metres (raw telemetry X/Z) - i.e. miles of actual
/// driving: one mile (1,609 engine metres) is about a minute at 65 mph. The odometer, the in-game GPS and the
/// Distance card count several miles for each, and how many varies with the road because it equals the game's
/// current time acceleration (measured live in ETS2: exactly 3 on slow roads and in towns, 15 on UK highways, 7-13
/// in transitions; a constant 16.35 in an ATS multiplayer convoy - single-player ATS and continental motorways not measured yet). So one mile here reads as roughly 3 to 16 or more
/// odometer miles, and the 200 m auto threshold as roughly 0.4 to 2.5. User-facing text (manual, README, Settings)
/// says "a mile" / "200 m" and explains the odometer difference once. See DisplayScaleEstimator.
///
/// Two modes, chosen by the user's "recompute the route automatically" setting:
///   - AUTOMATIC (the old behaviour): beyond 200 m the route is recomputed immediately.
///   - MANUAL (the default): TruckHub never recomputes by itself. A fuel stop, a rest area down an access
///     road or a service station is not "lost" - the in-game GPS gives leeway for a U-turn, and so does
///     this. Only when the truck is a full mile from the route does TruckHub raise the OFF ROUTE alert
///     (red warning + Reroute button on the map), and the user chooses when to recompute (button, or the
///     Ctrl+Alt+F8 hotkey). The alert clears once the truck is back on the route.
/// </summary>
public sealed class OffRouteMonitor
{
    /// <summary>Automatic mode: farther than this from the route triggers a recompute. Unchanged from before.</summary>
    public const double RecomputeThresholdMeters = 200.0;

    /// <summary>Manual mode: the OFF ROUTE alert only appears at this distance - one mile of actual driving, which
    /// the in-game odometer counts as roughly 3 to 16 or more miles depending on the road (see the UNITS note above).</summary>
    public const double AlertThresholdMeters = 1609.344;

    /// <summary>Manual mode: once alerted, the alert clears only when the truck is back within this of the
    /// route (so it doesn't flicker as the truck wanders between 200 m and a mile on its way back).</summary>
    public const double ClearThresholdMeters = RecomputeThresholdMeters;

    /// <summary>True while the OFF ROUTE alert should be showing (manual mode only).</summary>
    public bool IsOffRoute { get; private set; }

    /// <summary>
    /// Call once per position tick with the truck's distance (metres) to the nearest point of the route.
    /// <paramref name="alertChanged"/> is true on the tick the alert turns on or off, so the caller can
    /// notify the UI exactly once per change.
    /// </summary>
    public OffRouteAction Evaluate(double distanceMeters, bool autoRecompute, out bool alertChanged)
    {
        alertChanged = false;

        if (autoRecompute)
        {
            // Automatic mode never shows the manual alert; if the user just switched to it mid-alert, drop it.
            if (IsOffRoute)
            {
                IsOffRoute = false;
                alertChanged = true;
            }

            return distanceMeters > RecomputeThresholdMeters ? OffRouteAction.RecomputeNow : OffRouteAction.None;
        }

        if (!IsOffRoute && distanceMeters >= AlertThresholdMeters)
        {
            IsOffRoute = true;
            alertChanged = true;
        }
        else if (IsOffRoute && distanceMeters <= ClearThresholdMeters)
        {
            IsOffRoute = false;
            alertChanged = true;
        }

        return OffRouteAction.None;
    }

    /// <summary>Drops the alert - a new route was requested or the route was cleared. Returns true if it was showing.</summary>
    public bool Reset()
    {
        if (!IsOffRoute)
        {
            return false;
        }

        IsOffRoute = false;
        return true;
    }
}
