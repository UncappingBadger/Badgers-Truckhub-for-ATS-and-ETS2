using System;

namespace TruckHub.Services;

/// <summary>What kind of sound the ALIGN beeps should be making right now.</summary>
public enum BeepKind
{
    /// <summary>Silence.</summary>
    None,
    /// <summary>The parking-sensor beep: a short tone repeated at a rate that rises as the pin gets closer.</summary>
    Proximity,
    /// <summary>At the pin: one steady tone.</summary>
    Continuous,
    /// <summary>Closing faster than the slow-down baseline: a distinct two-tone pair, repeated.</summary>
    TooFast,
    /// <summary>The pin is behind you now (you have gone too far under the trailer): rapid low beeps, "pull forward".</summary>
    PastHook,
}

/// <summary>
/// One moment of the beeps: the kind, the gap between beeps (Proximity / TooFast / PastHook), the pitch (0 lined up, 1 almost,
/// 2 off line - the same three zones as the colours) and the stereo position (-1 = left speaker, +1 = right).
/// </summary>
public readonly record struct BeepCue(BeepKind Kind, double IntervalSeconds, int Pitch, double Pan)
{
    public static BeepCue None { get; } = new(BeepKind.None, 0.0, 0, 0.0);
}

/// <summary>
/// Turns what the ALIGN window knows into the beeps, the way a parking sensor does - and, like the picture, from measured
/// geometry only: the distance and offsets come from the hub's hook points and the two vehicles' positions, which are right
/// from the very first hook-up (they are not learned). Nothing here uses the learned steering model.
///
///   distance   straight-line distance on the ground from the fifth wheel to the kingpin (not just the gap behind you, so
///              an approach that is off to the side does not sound as if it had arrived)
///   rate       slow and steady at 5 m, faster and faster, continuous at 0.5 m or less
///   pitch      by how well lined up you are (the green / amber / red zones)
///   pan        toward the side the kingpin is on (kingpin to your left = left speaker)
///   too fast   a different sound entirely when the slow-down warning is up
///   past it    rapid low beeps when the pin is behind the fifth wheel
///
/// All of it only while the truck is in a reverse gear.
///
/// The numbers come from 14 recorded hook-ups: drivers stopped at a median 0.34 m from the hook and crept at about 0.7 mph.
/// </summary>
public static class AlignBeepPlanner
{
    /// <summary>The beeps start this far out (the same 5 m as the slow-down warning).</summary>
    public const double RangeM = 5.0;

    /// <summary>At or inside this distance it is one steady tone.</summary>
    public const double ContinuousM = 0.5;

    /// <summary>The pin counts as "under the truck" (past the hook) when it is this close to the truck's centreline.</summary>
    public const double PastHookWidthM = 0.6;

    /// <summary>Sideways offset at which the sound is fully in one speaker, and the offset inside which it stays centred.</summary>
    public const double PanFullM = 0.5;
    public const double PanDeadZoneM = 0.03;

    public const double TooFastIntervalSeconds = 0.50;
    public const double PastHookIntervalSeconds = 0.10;

    // distance (m) -> seconds between beeps, joined by straight lines. Beyond the ends it holds the end value.
    private static readonly (double Distance, double Interval)[] Curve =
    {
        (0.6, 0.12),
        (1.0, 0.25),
        (2.0, 0.50),
        (5.0, 1.00),
    };

    public static BeepCue Plan(HitchFrame f)
    {
        // Only while reversing onto the trailer: a truck in neutral or a forward gear (pulling away, waiting, rolling) is silent.
        if (f.State != HitchState.Approaching || !f.InRearView || f.Paused || !f.InReverseGear)
        {
            return BeepCue.None;
        }

        var pan = PanFor(f.LeftM);
        var pitch = f.LinedUp ? 0 : (f.SideZone == HitchZone.Red || f.HeightZone == HitchZone.Red ? 2 : 1);

        if (f.BehindM < HitchTolerance.OvershootM && Math.Abs(f.LeftM) <= PastHookWidthM)
        {
            return new BeepCue(BeepKind.PastHook, PastHookIntervalSeconds, 2, pan);
        }

        var distance = Math.Sqrt(f.BehindM * f.BehindM + f.LeftM * f.LeftM);
        if (distance > RangeM)
        {
            return BeepCue.None;
        }

        if (f.SlowDown)
        {
            return new BeepCue(BeepKind.TooFast, TooFastIntervalSeconds, pitch, pan);
        }

        if (distance <= ContinuousM && f.BehindM >= HitchTolerance.OvershootM)
        {
            return new BeepCue(BeepKind.Continuous, 0.0, pitch, pan);
        }

        return new BeepCue(BeepKind.Proximity, IntervalFor(distance), pitch, pan);
    }

    /// <summary>Seconds between beeps at this distance (metres): shorter the closer you are.</summary>
    public static double IntervalFor(double distanceM)
    {
        if (distanceM <= Curve[0].Distance)
        {
            return Curve[0].Interval;
        }

        for (var i = 1; i < Curve.Length; i++)
        {
            if (distanceM <= Curve[i].Distance)
            {
                var (d0, t0) = Curve[i - 1];
                var (d1, t1) = Curve[i];
                return t0 + (t1 - t0) * (distanceM - d0) / (d1 - d0);
            }
        }

        return Curve[^1].Interval;
    }

    /// <summary>-1 (left speaker) to +1 (right) from the kingpin's offset: kingpin to the truck's left sounds on the left.</summary>
    public static double PanFor(double leftM)
    {
        if (Math.Abs(leftM) <= PanDeadZoneM)
        {
            return 0.0;
        }

        return Math.Clamp(-leftM / PanFullM, -1.0, 1.0);
    }
}
