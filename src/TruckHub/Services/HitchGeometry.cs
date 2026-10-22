using System;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>What the ALIGN window has to say right now.</summary>
public enum HitchState
{
    /// <summary>The game is not running / the telemetry hub is not answering.</summary>
    NoData,
    /// <summary>A trailer is there but the hub did not send the hook points - an old telemetry plugin.</summary>
    NeedsPlugin,
    /// <summary>No trailer in the game's trailer slot (none assigned, or it was taken away).</summary>
    NoTrailer,
    /// <summary>Hooked up. The error is ~0 by definition; the yaw is the articulation angle.</summary>
    Coupled,
    /// <summary>A trailer standing unhooked - a dropped one, or a job's yard trailer - to be backed onto.</summary>
    Approaching,
}

public enum HitchZone { Green, Amber, Red }

/// <summary>
/// How close is close enough. These come from the live hook-up measurements (ETS2 + ATS, 2026-10-18/20): with the
/// stricter coupling the game caught a kingpin that was at most 5.4 cm off sideways and 4.5 cm off in height, and
/// refused one that was ~20 cm off sideways. The edges of the window are therefore deliberately a little inside what
/// was proven to work, and the amber band covers "worth correcting before you get close".
/// </summary>
public static class HitchTolerance
{
    public const double GreenSideM = 0.05;
    public const double AmberSideM = 0.15;
    public const double GreenHeightM = 0.05;
    public const double AmberHeightM = 0.10;

    /// <summary>Gap (kingpin behind the fifth wheel) at which the truck counts as "at the hook" - the game closes up to
    /// ~0.6 m by itself in basic coupling, so this is not a precision figure.</summary>
    public const double HookGapM = 0.60;

    /// <summary>Kingpin this far AHEAD of the fifth wheel = the truck has gone too far under the trailer.</summary>
    public const double OvershootM = -0.30;

    public static HitchZone Side(double sideMeters) => Classify(Math.Abs(sideMeters), GreenSideM, AmberSideM);

    public static HitchZone Height(double upMeters) => Classify(Math.Abs(upMeters), GreenHeightM, AmberHeightM);

    private static HitchZone Classify(double abs, double green, double amber) =>
        abs <= green ? HitchZone.Green : abs <= amber ? HitchZone.Amber : HitchZone.Red;
}

/// <summary>
/// The kingpin's position relative to the truck's fifth wheel, in the truck's own horizontal frame, plus what the
/// ALIGN camera view needs to draw it. Camera frame: the camera sits at the fifth wheel looking straight back along
/// the truck; "right" is the truck's right, "back" is metres behind the fifth wheel (negative = ahead of it).
/// </summary>
public sealed record HitchFrame
{
    public HitchState State { get; init; }

    /// <summary>How far the kingpin is BEHIND the fifth wheel along the truck's heading, metres. Negative = ahead of it.</summary>
    public double BehindM { get; init; }

    /// <summary>Kingpin to the truck's LEFT (+) or RIGHT (-) of the truck's centreline, metres.</summary>
    public double LeftM { get; init; }

    /// <summary>Kingpin ABOVE (+) or BELOW (-) the fifth wheel, metres (world height difference).</summary>
    public double UpM { get; init; }

    /// <summary>Trailer heading minus truck heading, degrees in (-180, 180]; positive = trailer turned to the truck's left.</summary>
    public double YawDeg { get; init; }

    /// <summary>The trailer's forward axis (pointing from the kingpin toward the trailer's nose) as a unit vector in the
    /// camera frame: AxisRight = its component to the truck's right, AxisBack = its component backwards.</summary>
    public double AxisRight { get; init; }
    public double AxisBack { get; init; }

    /// <summary>What to draw for the trailer itself (a generic hauler unless the snapshot says more).</summary>
    public TrailerShape Shape { get; init; } = TrailerShape.Generic;

    /// <summary>Where the fifth wheel will go if the truck reverses with the wheel as it is now - the guide lines follow it.
    /// Null means "draw them straight back": the steering model is not learned yet, the truck is driving forward, or it is hooked up.</summary>
    public ReversePath? Path { get; init; }

    /// <summary>How fast the truck is reversing right now, m/s (0 when not reversing).</summary>
    public double ClosingSpeedMps { get; init; }

    /// <summary>Backing onto the trailer faster than the slow-down baseline (see <see cref="ApproachSpeed"/>): the window warns.</summary>
    public bool SlowDown { get; init; }

    /// <summary>The game is paused: nothing is moving, so the beeps stay quiet.</summary>
    public bool Paused { get; init; }

    /// <summary>The truck is in a reverse gear (the game's displayed gear is negative - the same for manual and automatic gearboxes). The parking-sensor beeps only sound then.</summary>
    public bool InReverseGear { get; init; }

    public double DistanceM => Math.Sqrt(BehindM * BehindM + LeftM * LeftM);

    /// <summary>The trailer is close enough behind the truck for the rear-camera picture to make sense.</summary>
    public bool InRearView =>
        State is HitchState.Approaching or HitchState.Coupled && BehindM >= -1.0 && BehindM <= 80.0;

    public HitchZone SideZone => HitchTolerance.Side(LeftM);
    public HitchZone HeightZone => HitchTolerance.Height(UpM);

    /// <summary>Sideways and height both inside the green band: the kingpin will go into the fifth wheel if you keep going straight back.</summary>
    public bool LinedUp => SideZone == HitchZone.Green && HeightZone == HitchZone.Green;

    public static HitchFrame Empty(HitchState state) => new() { State = state };
}

/// <summary>
/// The hitch maths, kept free of any UI so it can be checked against real recordings. A vehicle's local point maps into
/// the world as origin + R(heading, pitch, roll) * point, with SCS vehicle space (x right, y up, z back), angles in turns,
/// roll about z, then pitch about x, then heading about y (counter-clockwise from north = -Z). That convention was
/// validated against a coupled ATS rig: the kingpin and fifth wheel coincide to ~1 mm using the hub's hook points.
/// </summary>
public static class HitchGeometry
{
    private const double Tau = 2.0 * Math.PI;

    public static HitchFrame Compute(TelemetrySnapshot s, double extrapolateSeconds = 0.0)
    {
        if (!s.SdkActive)
        {
            return HitchFrame.Empty(HitchState.NoData);
        }

        var attached = s.TrailerAttached;
        if (s.TrailerPose.IsZero)
        {
            return HitchFrame.Empty(HitchState.NoTrailer);
        }

        if (s.TruckHookPoint is not { } fifthWheel || s.TrailerHookPoint is not { } kingpin)
        {
            return HitchFrame.Empty(HitchState.NeedsPlugin);
        }

        var truck = s.TruckPose;
        if (extrapolateSeconds > 0 && !s.Paused && !attached)
        {
            // The hub speaks at 10 Hz; a truck backing at 0.7 m/s moves 7 cm between two updates - more than the
            // sideways tolerance - so carry the truck forward along its own heading until the next one arrives.
            var v = s.SpeedKph / 3.6 * Math.Min(extrapolateSeconds, 0.25);
            var h = Tau * truck.Heading;
            truck = truck with { X = truck.X - Math.Sin(h) * v, Z = truck.Z - Math.Cos(h) * v };
        }

        return Compute(truck, fifthWheel, s.TrailerPose, kingpin, attached ? HitchState.Coupled : HitchState.Approaching)
            with { Shape = TrailerShape.From(s), ClosingSpeedMps = ApproachSpeed.ClosingMps(s), Paused = s.Paused, InReverseGear = s.GearDashboardsRaw < 0 };
    }

    public static HitchFrame Compute(VehiclePose truck, LocalPoint fifthWheel, VehiclePose trailer, LocalPoint kingpin, HitchState state)
    {
        var (fx, fy, fz) = World(truck, fifthWheel);
        var (kx, ky, kz) = World(trailer, kingpin);
        double dx = kx - fx, dy = ky - fy, dz = kz - fz;

        // Truck's horizontal frame. Heading 0 faces north (-Z), counter-clockwise positive, so forward = (-sin, -cos) in
        // (X, Z) and the truck's left = (-cos, sin).
        var th = Tau * truck.Heading;
        double fwdX = -Math.Sin(th), fwdZ = -Math.Cos(th);
        double leftX = -Math.Cos(th), leftZ = Math.Sin(th);

        var behind = -(dx * fwdX + dz * fwdZ);
        var left = dx * leftX + dz * leftZ;

        // The trailer's forward axis, expressed in the camera frame (right = -left, back = -forward).
        var tt = Tau * trailer.Heading;
        double axX = -Math.Sin(tt), axZ = -Math.Cos(tt);
        var axisRight = -(axX * leftX + axZ * leftZ);
        var axisBack = -(axX * fwdX + axZ * fwdZ);

        var yaw = (trailer.Heading - truck.Heading) % 1.0;
        if (yaw > 0.5) yaw -= 1.0;
        if (yaw <= -0.5) yaw += 1.0;

        return new HitchFrame
        {
            State = state,
            BehindM = behind,
            LeftM = left,
            UpM = dy,
            YawDeg = yaw * 360.0,
            AxisRight = axisRight,
            AxisBack = axisBack,
        };
    }

    /// <summary>World position of a point given in a vehicle's local space.</summary>
    public static (double X, double Y, double Z) World(VehiclePose pose, LocalPoint p)
    {
        double heading = Tau * pose.Heading, pitch = Tau * pose.Pitch, roll = Tau * pose.Roll;
        double x = p.X, y = p.Y, z = p.Z;

        // roll about z
        var x1 = x * Math.Cos(roll) - y * Math.Sin(roll);
        var y1 = x * Math.Sin(roll) + y * Math.Cos(roll);
        // pitch about x
        var y2 = y1 * Math.Cos(pitch) - z * Math.Sin(pitch);
        var z2 = y1 * Math.Sin(pitch) + z * Math.Cos(pitch);
        // heading about y
        var x3 = x1 * Math.Cos(heading) + z2 * Math.Sin(heading);
        var z3 = -x1 * Math.Sin(heading) + z2 * Math.Cos(heading);

        return (pose.X + x3, pose.Y + y2, pose.Z + z3);
    }
}
