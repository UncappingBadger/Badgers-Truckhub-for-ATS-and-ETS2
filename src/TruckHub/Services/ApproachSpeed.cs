using System;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// When the ALIGN window tells you to slow down. The game's collision damage grows with the SQUARE of the impact speed and
/// has no minimum speed in its definitions (def.scs vehicle_damage_data: coefficient 0.0007 x speed squared, weighted), so
/// there is no safe speed to find - only a baseline: reversing faster than 2 mph (0.89 m/s, 3.2 km/h) when the trailer is
/// within 5 m. A baseline to adjust once real hits have been measured. The speed is the truck's own speed backward along its
/// heading, which for backing onto a parked trailer is how fast the pin is closing.
///
/// Tuned to flicker as little as possible: it warns at 2 mph and lets go at 90 % of that, so riding the line does not blink.
/// </summary>
public static class ApproachSpeed
{
    public const double MetresPerSecondPerMph = 0.44704;

    /// <summary>The baseline trigger.</summary>
    public const double WarnAtMph = 2.0;

    /// <summary>The trigger in metres per second.</summary>
    public const double WarnMps = WarnAtMph * MetresPerSecondPerMph;

    /// <summary>Once warning, it stays until the speed drops below this.</summary>
    public const double ClearMps = WarnMps * 0.9;

    /// <summary>Only when the kingpin is within this many metres behind the fifth wheel: further out there is nothing to hit yet.</summary>
    public const double WithinMetres = 5.0;

    /// <summary>How fast the truck is reversing, metres per second (0 when driving forward, stopped, paused or the game is not running).</summary>
    public static double ClosingMps(TelemetrySnapshot s) =>
        s.SdkActive && !s.Paused ? Math.Max(0.0, -s.SpeedKph / 3.6) : 0.0;

    /// <summary>Backing toward an unhooked trailer, near it, not already past the hook point.</summary>
    public static bool InZone(HitchFrame f) =>
        f.State == HitchState.Approaching && f.InRearView && f.BehindM <= WithinMetres && f.BehindM >= HitchTolerance.OvershootM;

    /// <summary>The next state of the warning, given the current one and the frame (which carries the closing speed).</summary>
    public static bool Update(bool warning, HitchFrame f)
    {
        if (!InZone(f))
        {
            return false;
        }

        return warning ? f.ClosingSpeedMps >= ClearMps : f.ClosingSpeedMps >= WarnMps;
    }
}
