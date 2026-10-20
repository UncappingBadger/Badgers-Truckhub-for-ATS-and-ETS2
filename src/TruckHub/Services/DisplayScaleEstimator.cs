using System;

namespace TruckHub.Services;

/// <summary>
/// Learns, from the truck's own telemetry, how many DISPLAYED metres the game counts per ENGINE metre right now.
///
/// Why this is needed: the route geometry and the truck's position are in the engine's world metres, but the
/// odometer, the in-game GPS and the Distance card count a much bigger "displayed" distance - and the ratio is not
/// a constant. It equals the game's current time acceleration, which depends on the road: measured live in ETS2's
/// UK it was exactly 3.00 on slow roads and in towns, exactly 15.00 on UK highways and 7-13 in the transitions
/// between; in an ATS multiplayer convoy it was a constant 16.35 for 3 hours, slow roads included - single-player ATS and
/// continental motorways are not measured yet). So a fixed factor is only right
/// on open highway; this estimates the factor of the road the truck is on.
///
/// Method: every position tick adds (odometer delta, engine distance moved) to two running sums that fade with
/// distance driven (so the estimate leans on the last few tens of engine metres), and
/// the estimate is their ratio. Standing still adds nothing, so the last value is kept at a red light. A jump (fast
/// travel, a save reload, a garage teleport) or an odometer that goes backwards starts it over. Until enough has been
/// driven it reports null and callers use a per-game default.
/// </summary>
public sealed class DisplayScaleEstimator
{
    /// <summary>How much recent driving the estimate leans on: older movement fades by 1/e every this many engine metres
    /// (about 1 s at highway speed, 5 s in town). Replayed over a real ETS2 drive, 15-30 gave the best prediction of the
    /// scale over the next 60-150 engine metres (90th-percentile error 4-5%, against 32% for 120, and 5-6x too high
    /// for a fixed 19.15); much shorter would start to show the odometer's own resolution (it is a 32-bit float,
    /// about 4 m at these values).</summary>
    public const double MemoryEngineMeters = 25.0;

    /// <summary>Below this much (faded) engine distance there is not enough to trust the ratio.</summary>
    public const double MinEngineMeters = 10.0;

    /// <summary>One tick moving further than this is a teleport, fast travel or a load - not driving.</summary>
    public const double MaxJumpEngineMeters = 300.0;

    /// <summary>Anything outside this is noise or a discontinuity, never a real road scale.</summary>
    public const double MinScale = 1.0;
    public const double MaxScale = 30.0;

    private bool _hasPrevious;
    private double _prevX, _prevZ, _prevOdometerMeters;
    private double _sumEngine, _sumDisplayed;

    /// <summary>The current displayed-per-engine ratio, or null while there is not enough driving to say.</summary>
    public double? Scale
    {
        get
        {
            if (_sumEngine < MinEngineMeters)
            {
                return null;
            }

            var ratio = _sumDisplayed / _sumEngine;
            return double.IsFinite(ratio) ? Math.Clamp(ratio, MinScale, MaxScale) : null;
        }
    }

    public void Reset()
    {
        _hasPrevious = false;
        _sumEngine = 0;
        _sumDisplayed = 0;
    }

    /// <summary>Feed the truck's engine position (telemetry X/Z) and odometer once per position tick.</summary>
    public void Update(double x, double z, double odometerKm)
    {
        var odometerMeters = odometerKm * 1000.0;
        if (!_hasPrevious)
        {
            Remember(x, z, odometerMeters);
            return;
        }

        var engine = Math.Sqrt((x - _prevX) * (x - _prevX) + (z - _prevZ) * (z - _prevZ));
        var displayed = odometerMeters - _prevOdometerMeters;

        if (engine > MaxJumpEngineMeters || displayed < 0 || (engine > 1.0 && displayed / engine > MaxScale * 1.5))
        {
            Reset();
            Remember(x, z, odometerMeters);
            return;
        }

        if (engine < 0.05)
        {
            // Not moved (or the position simply hasn't updated yet): leave the previous point alone so whatever the
            // odometer did in the meantime is counted against the movement that follows, not lost.
            return;
        }

        var fade = Math.Exp(-engine / MemoryEngineMeters);
        _sumEngine = _sumEngine * fade + engine;
        _sumDisplayed = _sumDisplayed * fade + displayed;
        Remember(x, z, odometerMeters);
    }

    private void Remember(double x, double z, double odometerMeters)
    {
        _prevX = x;
        _prevZ = z;
        _prevOdometerMeters = odometerMeters;
        _hasPrevious = true;
    }
}
