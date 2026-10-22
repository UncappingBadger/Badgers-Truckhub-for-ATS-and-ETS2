using System;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Owns the steering calibrator and its memory: feeds it every telemetry snapshot and keeps what it learns per truck, so each
/// drive carries on from the last. Saved about once a minute while there is new driving to add (100+ samples), and by
/// <see cref="Flush"/> when the app closes. The clock is injectable only so the saving rule can be tested.
/// </summary>
public sealed class SteeringLearner
{
    private const long SaveEveryMs = 60_000;
    private const long MinNewSamplesToSave = 100;

    private readonly ISteeringCalibrationStore? _store;
    private readonly Func<long> _clockMs;
    private long _lastSaveMs;
    private string _savedKey = "";
    private long _savedSamples;

    /// <param name="store">Where each truck's learning is kept between runs; null = learn afresh every run.</param>
    /// <param name="clockMs">Milliseconds, steadily increasing; defaults to the system tick count.</param>
    public SteeringLearner(ISteeringCalibrationStore? store, Func<long>? clockMs = null)
    {
        _store = store;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _lastSaveMs = _clockMs();
        Calibrator = new SteeringCalibrator(store is null ? null : store.Get);
    }

    public SteeringCalibrator Calibrator { get; }

    /// <summary>Feed every telemetry snapshot (whether or not the ALIGN panel is open, so it is ready when it is opened).</summary>
    public void Observe(TelemetrySnapshot snapshot)
    {
        Calibrator.Observe(_clockMs() / 1000.0, snapshot);
        Save(force: false);
    }

    /// <summary>Save now if there is anything new (called when the app closes).</summary>
    public void Flush() => Save(force: true);

    private void Save(bool force)
    {
        if (_store is null || !Calibrator.IsReady || Calibrator.Snapshot() is not { } stats || Calibrator.Geometry is not { } geometry)
        {
            return;
        }

        var key = Calibrator.Key;
        var newSamples = Calibrator.AcceptedSamples - (key == _savedKey ? _savedSamples : 0);
        if (newSamples <= 0)
        {
            return;
        }

        var now = _clockMs();
        if (!force && (now - _lastSaveMs < SaveEveryMs || newSamples < MinNewSamplesToSave))
        {
            return;
        }

        _store.Put(key, geometry, stats);
        _savedKey = key;
        _savedSamples = Calibrator.AcceptedSamples;
        _lastSaveMs = now;
    }
}
