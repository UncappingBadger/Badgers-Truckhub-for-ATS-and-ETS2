using System;
using System.ComponentModel;
using System.Windows.Threading;
using TruckHub.Models;
using TruckHub.Services;

namespace TruckHub.ViewModels;

/// <summary>
/// Backs the ALIGN window - the hitch alignment view (docked above the main window or popped out). One instance is
/// shared by the docked panel and the pop-out, the same way the job log shares the main view model.
///
/// The telemetry hub speaks at 10 Hz, but a truck backing at walking pace moves several centimetres between two
/// updates - more than the sideways tolerance - so while the panel is on screen a timer re-derives the picture ~30
/// times a second, carrying the truck forward along its own heading from the last snapshot (see
/// <see cref="HitchGeometry.Compute(TelemetrySnapshot, double)"/>). When the panel is closed nothing runs at all.
/// </summary>
public sealed class AlignViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Driving forward faster than this (1 m/s) puts the guide lines back to straight: a reversing prediction means nothing then.</summary>
    private const float ForwardCutoffKph = 3.6f;

    private readonly Func<bool> _useMetric;
    private readonly SteeringLearner _learner;
    private readonly IBeepOutput? _beepOutput;
    private readonly AlignBeeper? _beeper;
    private readonly DispatcherTimer _timer;
    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Disconnected;
    private long _snapshotTick = Environment.TickCount64;
    private bool _active;
    private bool _slowDown;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="useMetric">Read live each time, so a unit change in Settings applies at once.</param>
    /// <param name="store">Where each truck's learned steering is kept between runs; null = learn afresh every run.</param>
    /// <param name="beepOutput">Where the parking-sensor beeps are played; null = no sound (what the tests use).</param>
    public AlignViewModel(Func<bool> useMetric, ISteeringCalibrationStore? store = null, IBeepOutput? beepOutput = null)
    {
        _useMetric = useMetric;
        _learner = new SteeringLearner(store);
        _beepOutput = beepOutput;
        _beeper = beepOutput is null ? null : new AlignBeeper(beepOutput);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Recompute();
    }

    /// <summary>What the truck's steering has taught us (and, once ready, what bends the guide lines).</summary>
    public SteeringCalibrator Steering => _learner.Calibrator;

    /// <summary>The Settings choice for the parking-sensor beeps: on/off and the 0-100 volume. They only ever sound while this window is open.</summary>
    public void ConfigureBeeps(bool enabled, int volumePercent) => _beeper?.Configure(enabled, volumePercent);

    public HitchFrame Frame { get; private set; } = HitchFrame.Empty(HitchState.NoData);
    public AlignReadout Readout { get; private set; } = AlignPresenter.Present(HitchFrame.Empty(HitchState.NoData), true);

    public string StatusText => Readout.StatusText;
    public string StatusColor => Readout.StatusColor;
    public string SideText => Readout.SideText;
    public string SideColor => Readout.SideColor;
    public string GapText => Readout.GapText;
    public string GapColor => Readout.GapColor;
    public string HeightText => Readout.HeightText;
    public string HeightColor => Readout.HeightColor;
    public string AngleText => Readout.AngleText;
    public string AngleColor => Readout.AngleColor;
    public bool ShowOverlayMessage => Readout.OverlayMessage;

    /// <summary>Backing in too fast: the status banner turns red and says so.</summary>
    public bool SlowDown => Frame.SlowDown;

    // Same "no info until a game's actually detected" behaviour as the main window and EXT: until the sim is connected the
    // panel shows only the waiting line - no picture, no empty readout tiles.
    public bool IsConnected => Frame.State != HitchState.NoData;
    public bool IsDisconnected => !IsConnected;
    public string WaitingMessage => AlignPresenter.WaitingForGame;

    /// <summary>True while the panel (docked or popped out) is on screen; starts and stops the refresh timer.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (value == _active)
            {
                return;
            }

            _active = value;
            if (value)
            {
                _timer.Start();
                Recompute();
            }
            else
            {
                _timer.Stop();
                _beeper?.Silence();
            }
        }
    }

    /// <summary>Feed every telemetry snapshot (UI thread).</summary>
    public void ApplySnapshot(TelemetrySnapshot snapshot)
    {
        _snapshot = snapshot;
        _snapshotTick = Environment.TickCount64;

        // The steering model learns from every snapshot whether or not the panel is open, so it is ready when it is opened.
        _learner.Observe(snapshot);

        if (_active)
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        var age = (Environment.TickCount64 - _snapshotTick) / 1000.0;
        var frame = HitchGeometry.Compute(_snapshot, age);
        _slowDown = ApproachSpeed.Update(_slowDown, frame);
        Frame = frame with { Path = BuildPath(frame), SlowDown = _slowDown };
        Readout = AlignPresenter.Present(Frame, _useMetric());
        _beeper?.Update(Environment.TickCount64 / 1000.0, AlignBeepPlanner.Plan(Frame), _snapshot.SpeedKph / 3.6);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>The predicted path for the guide lines, or null to draw them straight back.</summary>
    private ReversePath? BuildPath(HitchFrame frame)
    {
        if (frame.State is HitchState.NoData or HitchState.NeedsPlugin or HitchState.Coupled
            || _snapshot.SpeedKph > ForwardCutoffKph
            || Steering.Geometry is not { } geometry)
        {
            return null;
        }

        var steer = SteeringCalibrator.MeanSteerTurns(_snapshot);
        return double.IsNaN(steer) ? null : ReversePath.From(steer, geometry);
    }

    public void Dispose()
    {
        _timer.Stop();
        _beeper?.Silence();
        (_beepOutput as IDisposable)?.Dispose();
        _learner.Flush();
    }
}
