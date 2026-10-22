using System;
using System.Collections.Generic;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Learns, from the truck's own driving, the two numbers <see cref="ReversePath"/> needs - the effective wheelbase and
/// where the fifth wheel sits against the rear-axle line - because the telemetry says which wheels steer but not where any
/// truck wheel is. Two straight-line fits, both over a ~0.35 s window of the truck's recent motion:
///
///   yaw rate            = speed * tan(steer) / L            -> L          (ATS W900, 4,000+ samples: R2 0.99, L about 7.2 m)
///   origin's side speed = yaw rate * (origin ahead of axles) -> where the rear-axle line is (R2 0.995)
///
/// and the fifth wheel's offset follows from where the hub says the fifth wheel is. The first fit is kept separately for
/// REVERSING and for driving forward, because the game does not steer the same both ways (the same W900: L 7.2 m reversing,
/// 6.5-6.75 m forward - using the forward number for the reversing prediction costs about 10 % of the curve). The path is
/// for reversing, so the reversing fit is used once it has enough samples; until then the forward fit stands in, scaled up
/// by the typical difference. The windows divide displacement by the same
/// elapsed time, so timestamp jitter cancels.
///
/// It gets better the more a truck is driven. The fits are running sums, not a single estimate, and the sums are what the
/// caller keeps per truck (<see cref="Snapshot"/> / <see cref="SteeringStats"/>) and hands back on the next launch
/// (<see cref="Restore"/>), so every drive adds to what earlier drives found instead of starting over. Samples fade slowly
/// (several minutes of driving), so one odd manoeuvre cannot move the answer, yet a changed truck still settles in time.
///
/// Not ready (<see cref="IsReady"/> false) means "keep drawing the lines straight back": it takes a few seconds of
/// driving with the wheel off-centre. Steering is the game's own unit - turns, positive = left.
/// </summary>
public sealed class SteeringCalibrator
{
    private const double Tau = 2.0 * Math.PI;

    private const double MinSpeed = 0.25;                       // m/s along the heading
    private const double MinSteerRadians = 3.0 * Math.PI / 180.0;
    private const double MinYawRate = 0.012;                    // rad/s, for the fifth-wheel-offset fit
    private const double Decay = 0.9997;                        // per accepted sample (~5.5 min of useful driving at the hub's 10 Hz)
    private const double ReadyWeight = 40.0;                    // accepted samples (~4 s of useful driving) before the fit is trusted
    private const double SeedWeight = 30.0;
    private const double MaxRestoredWeight = 6000.0;            // a saved weight can never exceed what the fade allows (1 / (1 - Decay) = 3333)

    private const double WindowMin = 0.30, WindowMax = 0.80, History = 1.6;   // seconds

    private const double MinWheelbase = 2.5, MaxWheelbase = 14.0, MaxFifthWheelOffset = 3.0;

    /// <summary>Reversing wheelbase / forward wheelbase, for the stretch before enough reversing has been seen.</summary>
    private const double ReverseFactor = 1.07;

    private readonly struct Sample
    {
        public readonly double T, X, Z, HeadingTurns, Steer;

        public Sample(double t, double x, double z, double headingTurns, double steer)
        {
            T = t; X = x; Z = z; HeadingTurns = headingTurns; Steer = steer;
        }
    }

    private readonly Func<string, SteeringRecord?>? _lookup;
    private readonly List<Sample> _samples = new();
    private double _unwrappedHeading, _lastHeading;
    private bool _haveHeading;
    private double _hookZ;
    private string _key = "";
    private bool _ready;

    // Sums for the fits (with fading): L: yaw = k * (v tan steer), kept apart for reversing (R) and forward (F);
    // offset: sideSpeed = c * yaw
    private double _rxx, _rxy, _rw;
    private double _fxx, _fxy, _fw;
    private double _cxx, _cxy, _cw;

    /// <param name="lookup">Optional: what was saved for a truck key on an earlier run, used to start a truck off already calibrated.</param>
    public SteeringCalibrator(Func<string, SteeringRecord?>? lookup = null) => _lookup = lookup;

    /// <summary>Identifies this truck/chassis: the game, the truck's id, where its fifth wheel is (chassis variants differ) and which wheels steer or drive.</summary>
    public string Key => _key;

    public bool IsReady => _ready;

    /// <summary>Samples taken into the fits since this truck was first seen this run; the caller saves again when it has moved on.</summary>
    public long AcceptedSamples { get; private set; }

    /// <summary>Accepted reversing samples behind the wheelbase fit (fading).</summary>
    public double ReversingWeight => _rw;

    /// <summary>The geometry for a REVERSING prediction.</summary>
    public SteeringGeometry? Geometry
    {
        get
        {
            if (!_ready || _cxx <= 0)
            {
                return null;
            }

            double wheelbase;
            if (_rw >= ReadyWeight && _rxy > 0)
            {
                wheelbase = _rxx / _rxy;
            }
            else if (_fxy > 0)
            {
                wheelbase = _fxx / _fxy * ReverseFactor;
            }
            else
            {
                return null;
            }

            var ahead = Math.Clamp(_cxy / _cxx - _hookZ, -MaxFifthWheelOffset, MaxFifthWheelOffset);
            return new SteeringGeometry(Math.Clamp(wheelbase, MinWheelbase, MaxWheelbase), ahead);
        }
    }

    public static string KeyFor(TelemetrySnapshot s) =>
        $"{s.Game}|{s.TruckId}|{(s.TruckHookPoint?.Z ?? 0f):0.000}|{Flags(s.TruckWheelSteerable)}|{Flags(s.TruckWheelPowered)}";

    private static string Flags(bool[] flags)
    {
        var chars = new char[flags.Length];
        for (var i = 0; i < flags.Length; i++)
        {
            chars[i] = flags[i] ? '1' : '0';
        }

        return new string(chars);
    }

    /// <summary>Mean angle of the steered wheels, turns, positive = left; NaN when the hub sent no wheel data.</summary>
    public static double MeanSteerTurns(TelemetrySnapshot s)
    {
        var wheels = s.TruckWheelSteering;
        double sum = 0;
        var n = 0;
        for (var i = 0; i < wheels.Length; i++)
        {
            // No steerable flags (an older hub): the front pair.
            var steered = s.TruckWheelSteerable.Length > i ? s.TruckWheelSteerable[i] : i < 2;
            if (steered)
            {
                sum += wheels[i];
                n++;
            }
        }

        return n > 0 ? sum / n : double.NaN;
    }

    /// <summary>Start the truck off from an earlier result (it still adapts to what it sees).</summary>
    public void Seed(string key, SteeringGeometry geometry, double hookZ)
    {
        Reset(key, hookZ);
        const double typicalLinearTerm = 0.2, typicalYawRate = 0.1;
        _rw = SeedWeight;
        _rxx = SeedWeight * typicalLinearTerm * typicalLinearTerm;
        _rxy = _rxx / geometry.WheelbaseM;
        _fw = SeedWeight;
        _fxx = _rxx;
        _fxy = _rxx / (geometry.WheelbaseM / ReverseFactor);
        _cw = SeedWeight;
        _cxx = SeedWeight * typicalYawRate * typicalYawRate;
        _cxy = _cxx * (geometry.FifthWheelAheadOfRearAxleM + hookZ);
        _ready = true;
    }

    /// <summary>The running sums behind the fits, to be kept per truck; null until the fits are ready.</summary>
    public SteeringStats? Snapshot() =>
        _ready ? new SteeringStats(_rxx, _rxy, _rw, _fxx, _fxy, _fw, _cxx, _cxy, _cw) : null;

    /// <summary>Carry on from the sums saved for this truck. Returns false (and leaves the truck unlearned) if they do not make sense.</summary>
    public bool Restore(string key, SteeringStats stats, double hookZ)
    {
        Reset(key, hookZ);
        double[] values = { stats.Rxx, stats.Rxy, stats.Rw, stats.Fxx, stats.Fxy, stats.Fw, stats.Cxx, stats.Cxy, stats.Cw };
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }
        }

        if (stats.Rxx < 0 || stats.Fxx < 0 || stats.Cxx < 0
            || stats.Rw < 0 || stats.Fw < 0 || stats.Cw < 0
            || stats.Rw > MaxRestoredWeight || stats.Fw > MaxRestoredWeight || stats.Cw > MaxRestoredWeight)
        {
            return false;
        }

        _rxx = stats.Rxx; _rxy = stats.Rxy; _rw = stats.Rw;
        _fxx = stats.Fxx; _fxy = stats.Fxy; _fw = stats.Fw;
        _cxx = stats.Cxx; _cxy = stats.Cxy; _cw = stats.Cw;
        TryMakeReady();
        if (!_ready)
        {
            Reset(key, hookZ);
        }

        return _ready;
    }

    private void Reset(string key, double hookZ)
    {
        _key = key;
        _hookZ = hookZ;
        _samples.Clear();
        _haveHeading = false;
        _rxx = _rxy = _rw = _fxx = _fxy = _fw = _cxx = _cxy = _cw = 0;
        _ready = false;
        AcceptedSamples = 0;
    }

    /// <summary>Feed every telemetry snapshot. <paramref name="seconds"/> is any steadily increasing clock.</summary>
    public void Observe(double seconds, TelemetrySnapshot s)
    {
        if (!s.SdkActive || s.Paused || s.TruckPose.IsZero || s.TruckHookPoint is not { } hook)
        {
            _samples.Clear();
            _haveHeading = false;
            return;
        }

        var steer = MeanSteerTurns(s);
        if (double.IsNaN(steer))
        {
            _samples.Clear();
            _haveHeading = false;
            return;
        }

        var key = KeyFor(s);
        if (key != _key)
        {
            Reset(key, hook.Z);
            if (_lookup?.Invoke(key) is { } saved)
            {
                // Prefer the saved running sums (it carries on learning); an older record that only has the two numbers seeds a weak start.
                if (saved.Stats is null || !Restore(key, saved.Stats, hook.Z))
                {
                    Seed(key, new SteeringGeometry(saved.WheelbaseM, saved.FifthWheelAheadM), hook.Z);
                }
            }
        }

        _hookZ = hook.Z;

        var heading = (double)s.TruckPose.Heading;
        if (!_haveHeading)
        {
            _unwrappedHeading = heading;
        }
        else
        {
            var d = heading - _lastHeading;
            _unwrappedHeading += d - Math.Round(d);
        }

        _lastHeading = heading;
        _haveHeading = true;

        _samples.Add(new Sample(seconds, s.TruckPose.X, s.TruckPose.Z, _unwrappedHeading, steer));
        while (_samples.Count > 1 && seconds - _samples[0].T > History)
        {
            _samples.RemoveAt(0);
        }

        var start = -1;
        for (var i = _samples.Count - 2; i >= 0; i--)
        {
            if (seconds - _samples[i].T >= WindowMin)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return;
        }

        var a = _samples[start];
        var b = _samples[^1];
        var dt = b.T - a.T;
        if (dt > WindowMax)
        {
            return;
        }

        var yaw = Tau * (b.HeadingTurns - a.HeadingTurns) / dt;                 // rad/s, positive = turning left
        var mid = Tau * 0.5 * (a.HeadingTurns + b.HeadingTurns);
        double fx = -Math.Sin(mid), fz = -Math.Cos(mid), lx = -Math.Cos(mid), lz = Math.Sin(mid);
        double dX = b.X - a.X, dZ = b.Z - a.Z;
        var forward = (dX * fx + dZ * fz) / dt;                                 // m/s along the heading, negative = reversing
        var side = (dX * lx + dZ * lz) / dt;                                    // m/s to the left, of the truck's origin
        if (Math.Abs(forward) < MinSpeed || Math.Abs(side) > 3.0)
        {
            return;
        }

        double steerSum = 0;
        for (var i = start; i < _samples.Count; i++)
        {
            steerSum += _samples[i].Steer;
        }

        var delta = Tau * steerSum / (_samples.Count - start);

        if (Math.Abs(delta) >= MinSteerRadians)
        {
            var x = forward * Math.Tan(delta);
            var reversing = forward < 0;
            ref var xx = ref (reversing ? ref _rxx : ref _fxx);
            ref var xy = ref (reversing ? ref _rxy : ref _fxy);
            ref var w = ref (reversing ? ref _rw : ref _fw);

            var accept = true;
            if (w >= 10 && xy > 0)
            {
                // Ignore the odd sample that is nowhere near the model (a bump, a slide, a shove from another vehicle).
                accept = Math.Abs(yaw - x * xy / xx) <= 0.04 + 0.35 * Math.Abs(yaw);
            }

            if (accept)
            {
                xx = xx * Decay + x * x;
                xy = xy * Decay + x * yaw;
                w = w * Decay + 1.0;
                AcceptedSamples++;
            }
        }

        if (Math.Abs(yaw) >= MinYawRate)
        {
            _cxx = _cxx * Decay + yaw * yaw;
            _cxy = _cxy * Decay + yaw * side;
            _cw = _cw * Decay + 1.0;
            AcceptedSamples++;
        }

        TryMakeReady();
    }

    /// <summary>Ready once there is enough behind both fits and the answer is a believable truck.</summary>
    private void TryMakeReady()
    {
        if (_ready || _cw < ReadyWeight || _cxx <= 0
            || !((_rw >= ReadyWeight && _rxy > 0) || (_fw >= ReadyWeight && _fxy > 0)))
        {
            return;
        }

        var wheelbase = _rw >= ReadyWeight && _rxy > 0 ? _rxx / _rxy : _fxx / _fxy * ReverseFactor;
        var originAhead = _cxy / _cxx;
        if (wheelbase >= MinWheelbase && wheelbase <= MaxWheelbase && originAhead > -2.0 && originAhead < 8.0
            && Math.Abs(originAhead - _hookZ) <= MaxFifthWheelOffset)
        {
            _ready = true;
        }
    }
}
