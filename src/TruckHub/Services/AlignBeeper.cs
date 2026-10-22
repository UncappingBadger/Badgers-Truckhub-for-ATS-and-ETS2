using System;

namespace TruckHub.Services;

/// <summary>Where the ALIGN beeps go. The real one plays through the sound card; tests record what was asked of it.</summary>
public interface IBeepOutput
{
    /// <summary>One short sound, right now, without blocking.</summary>
    void Play(BeepSound sound);

    /// <summary>Start a steady tone that carries on until <see cref="StopLoop"/> or the next <see cref="PlayLoop"/>.</summary>
    void PlayLoop(BeepSound sound);

    void StopLoop();

    /// <summary>The 0-100 volume the sounds are rendered at; a change takes effect from the next sound.</summary>
    void SetVolume(int volumePercent);
}

/// <summary>
/// Decides WHEN the beeps sound: a beep whenever the current gap (set by the planner from the distance) has passed since the
/// last one, so the rate rises smoothly as you close in; one steady tone at the pin. Called every frame (~30 times a second)
/// from the ALIGN window while it is open, with the cue for that frame.
///
/// Parking sensors go quiet when you stop and stay stopped, and so does this: a stationary truck is silent after
/// <see cref="StillMuteSeconds"/> until it moves again. The stereo position is kept from flickering between steps by
/// requiring the pan to move a little past the halfway point before it changes.
/// </summary>
public sealed class AlignBeeper
{
    public const double StillMuteSeconds = 2.5;
    public const double StillSpeedMps = 0.05;
    private const double LoopChangeMinSeconds = 0.4;
    private const double PanHysteresis = 0.15;           // in pan steps (half-steps are 0.5 apart on the -2..2 scale)

    private readonly IBeepOutput _output;
    private bool _enabled;
    private int _volume = 50;
    private double _lastBeepAt = double.NegativeInfinity;
    private BeepKind _lastKind = BeepKind.None;
    private double? _stillSince;
    private BeepSound? _loop;
    private double _loopStartedAt = double.NegativeInfinity;
    private int _panIndex;

    public AlignBeeper(IBeepOutput output) => _output = output;

    public bool Enabled => _enabled && _volume > 0;

    /// <summary>The Settings choice: on/off and the 0-100 volume (0 is silent, like the speed alerts).</summary>
    public void Configure(bool enabled, int volumePercent)
    {
        _enabled = enabled;
        _volume = Math.Clamp(volumePercent, 0, 100);
        _output.SetVolume(_volume);
        if (!Enabled)
        {
            Silence();
        }
    }

    /// <summary>Stop everything (the window closed, the game went away, or the beeps were switched off).</summary>
    public void Silence()
    {
        if (_loop is not null)
        {
            _output.StopLoop();
            _loop = null;
        }

        _stillSince = null;
        _lastKind = BeepKind.None;
    }

    /// <param name="now">Seconds, steadily increasing.</param>
    /// <param name="cue">What the planner wants this frame.</param>
    /// <param name="speedMps">The truck's speed (sign does not matter); used to go quiet when it stops.</param>
    public void Update(double now, BeepCue cue, double speedMps)
    {
        if (!Enabled || cue.Kind == BeepKind.None)
        {
            Silence();
            return;
        }

        if (Math.Abs(speedMps) < StillSpeedMps)
        {
            _stillSince ??= now;
            if (now - _stillSince.Value >= StillMuteSeconds)
            {
                StopLoopOnly();
                return;
            }
        }
        else
        {
            _stillSince = null;
        }

        _panIndex = StickyPan(cue.Pan);

        if (cue.Kind == BeepKind.Continuous)
        {
            _lastKind = BeepKind.Continuous;
            var wanted = new BeepSound(BeepKind.Continuous, cue.Pitch, _panIndex);
            if (_loop != wanted && now - _loopStartedAt >= LoopChangeMinSeconds)
            {
                _output.PlayLoop(wanted);
                _loop = wanted;
                _loopStartedAt = now;
            }

            return;
        }

        StopLoopOnly();

        // A change of kind (a warning coming on, leaving the steady tone) sounds at once instead of waiting out the old gap.
        if (cue.Kind != _lastKind || now - _lastBeepAt >= cue.IntervalSeconds)
        {
            _output.Play(new BeepSound(cue.Kind, cue.Pitch, _panIndex));
            _lastBeepAt = now;
            _lastKind = cue.Kind;
        }
    }

    private void StopLoopOnly()
    {
        if (_loop is not null)
        {
            _output.StopLoop();
            _loop = null;
        }
    }

    /// <summary>The stereo step for this pan, moving only when the pan is clearly past the halfway point to the next step.</summary>
    private int StickyPan(double pan)
    {
        var scaled = Math.Clamp(pan, -1.0, 1.0) * 2.0;
        if (Math.Abs(scaled - _panIndex) > 0.5 + PanHysteresis)
        {
            return AlignBeepSynth.PanIndexFor(pan);
        }

        return _panIndex;
    }
}
