using System;

namespace TruckHub.Services;

/// <summary>
/// The two numbers that decide where a truck's fifth wheel goes when it is steered (the "bicycle model"): the effective
/// wheelbase - the distance from the steering axle to the line the rear axles pivot about - and how far the fifth wheel
/// sits AHEAD of that line (negative = behind it, which is the usual case).
/// </summary>
public readonly record struct SteeringGeometry(double WheelbaseM, double FifthWheelAheadOfRearAxleM);

/// <summary>A point on a <see cref="ReversePath"/> in the ALIGN camera frame (right of the truck's centreline, back from the
/// fifth wheel), plus the unit normal to the path there so the two guide lines can be offset sideways from it.</summary>
public readonly record struct PathPoint(double Right, double Back, double NormalRight, double NormalBack)
{
    /// <summary>The point <paramref name="sideways"/> metres off the path (positive = toward the truck's right), as (right, back).</summary>
    public (double Right, double Back) Offset(double sideways) => (Right + sideways * NormalRight, Back + sideways * NormalBack);
}

/// <summary>
/// Where the fifth wheel will go if the truck reverses with the wheel held where it is now - what the ALIGN guide lines draw,
/// the way a pickup's reversing camera bends its lines with the steering wheel.
///
/// The truck is a rigid body turning about an instant centre on its rear-axle line, with curvature kappa = tan(steer) / L.
/// Reversing, the rear-axle point R travels backward along that circle and the fifth wheel, a fixed distance ahead of or
/// behind R, rides with it. Checked against a recorded ATS session (steering held, 3 m of reversing): median error 3 cm,
/// 90th percentile 14 cm against 30 cm for straight lines; fed the recorded steering instead of a held value it is within
/// 1 cm - so what is left is the driver changing the wheel, which is the point of showing it.
///
/// Distances along the path are U: metres travelled backward by the rear axle (negative = forward). Everything is
/// returned in the camera frame, relative to where the fifth wheel is now.
/// </summary>
public sealed class ReversePath
{
    private const double Tau = 2.0 * Math.PI;

    /// <summary>The steering angle is clamped here before the tangent so a bad reading cannot send the maths off to infinity.</summary>
    private const double MaxSteerRadians = 50.0 * Math.PI / 180.0;

    /// <summary>The path stops after the truck has turned this far (radians, ~46 degrees): a hard turn gets a short, tight
    /// curve, a gentle one a long line, and the far end never sweeps sideways across the whole picture. (Past ~90 degrees the
    /// circle would come back toward the truck.)</summary>
    private const double MaxHeadingChange = 0.8;

    /// <summary>1 / turning radius, per metre; positive = wheels turned to the truck's left.</summary>
    public double Kappa { get; }

    /// <summary>How far the fifth wheel is ahead of the rear-axle line, metres (negative = behind it).</summary>
    public double FifthWheelAheadM { get; }

    /// <summary>The path is defined (and drawn) for U up to this.</summary>
    public double MaxU { get; }

    public ReversePath(double kappa, double fifthWheelAheadM, double maxU)
    {
        Kappa = kappa;
        FifthWheelAheadM = fifthWheelAheadM;
        MaxU = maxU;
    }

    /// <summary>Straight back: what the guides show before the steering model is known, driving forward, or hooked up.</summary>
    public static ReversePath Straight { get; } = new(0.0, 0.0, 30.0);

    /// <param name="steerTurns">Mean angle of the steered wheels in turns, positive = left (the game's own unit).</param>
    public static ReversePath From(double steerTurns, SteeringGeometry geometry, double maxU = 30.0)
    {
        var steer = Math.Clamp(Tau * steerTurns, -MaxSteerRadians, MaxSteerRadians);
        var kappa = Math.Tan(steer) / geometry.WheelbaseM;
        var turnLimit = Math.Abs(kappa) > 1e-6 ? MaxHeadingChange / Math.Abs(kappa) : double.PositiveInfinity;
        return new ReversePath(kappa, geometry.FifthWheelAheadOfRearAxleM, Math.Min(maxU, turnLimit));
    }

    /// <summary>The fifth wheel's position after the rear axle has travelled <paramref name="u"/> metres backward.</summary>
    public PathPoint At(double u)
    {
        // Vehicle frame at the start: x forward, y left, origin on the rear-axle line. Reversing means the travelled
        // distance along the heading is -u, so the heading turns by psi = -kappa * u.
        var psi = -Kappa * u;
        double xr, yr;
        if (Math.Abs(Kappa) < 1e-6)
        {
            xr = -u;
            yr = 0.5 * Kappa * u * u;
        }
        else
        {
            xr = Math.Sin(psi) / Kappa;
            yr = (1.0 - Math.Cos(psi)) / Kappa;
        }

        var c = FifthWheelAheadM;
        double cos = Math.Cos(psi), sin = Math.Sin(psi);

        // Displacement of the fifth wheel from where it started, which was at (c, 0).
        double dx = xr + c * cos - c, dy = yr + c * sin;

        // Direction of travel (d/du of the position), then its left-hand normal.
        double tx = -cos + c * Kappa * sin, ty = -sin - c * Kappa * cos;
        var len = Math.Sqrt(tx * tx + ty * ty);
        if (len < 1e-9)
        {
            tx = -1.0; ty = 0.0; len = 1.0;
        }

        tx /= len; ty /= len;
        double nx = -ty, ny = tx;

        // Camera frame: back = -forward, right = -left.
        return new PathPoint(-dy, -dx, -ny, -nx);
    }
}
