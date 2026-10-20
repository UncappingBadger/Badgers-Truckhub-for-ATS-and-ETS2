using System;

namespace TruckHub.Services;

public enum SpeedLimitChange
{
    None,
    Increased,
    Decreased,
}

/// <summary>
/// Decides WHEN the two speed cues fire - the sign's border flash when the posted limit changes, and
/// the double beep when the player goes past the amber band. Pure logic (no WPF, no audio, no clock
/// of its own) so it can be exercised exactly as the app uses it; MainViewModel feeds it each snapshot
/// and acts on what it returns.
/// </summary>
public sealed class SpeedLimitAlerts
{
    /// <summary>
    /// Display units (mph or km/h, whichever the HUD is set to) over the limit that still count as
    /// the AMBER band. Past it the speed readout turns red - and this is also exactly where the beep
    /// fires, so what you hear and what you see can never disagree. MainViewModel.SpeedColor uses the
    /// same constant.
    /// </summary>
    public const int AmberBandUnits = 5;

    // The speed has to fall this far back inside the amber band before the beep can fire again, so
    // hovering right on the threshold doesn't chatter.
    private const int RearmMarginUnits = 2;

    // Real limit changes are whole 5 mph / 10 km/h steps; this only swallows float noise.
    private const float LimitChangeToleranceKph = 1.0f;

    // Hard floor between beeps, however the speed wanders.
    private static readonly TimeSpan MinBeepInterval = TimeSpan.FromSeconds(8);

    // Limits can change several times in quick succession (town centres, junctions); never beep for the
    // limit more often than this, whatever the road does.
    private static readonly TimeSpan MinLimitBeepInterval = TimeSpan.FromSeconds(3);

    private float? _lastLimitKph;
    private bool _speedBaselined;
    private bool _beepArmed = true;
    private DateTime _lastBeepUtc = DateTime.MinValue;
    private DateTime _lastLimitBeepUtc = DateTime.MinValue;

    /// <summary>
    /// Call just before sounding the limit-change beep for a change ObserveLimit reported. Returns false
    /// if the previous limit beep was less than 3 s ago, so a run of rapid limit changes gives one beep
    /// per burst rather than a stutter (the change itself is still tracked, only the sound is skipped).
    /// </summary>
    public bool AllowLimitChangeBeep(DateTime nowUtc)
    {
        if (nowUtc - _lastLimitBeepUtc < MinLimitBeepInterval)
        {
            return false;
        }

        _lastLimitBeepUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// Call every snapshot. Compares the raw limit in km/h (not the displayed number, so flipping the
    /// HUD between mph and km/h never reads as a limit change). Losing the limit data - game closed,
    /// route advisor off, loading - forgets the last value, so the limit simply coming back is not
    /// reported as a change either.
    /// </summary>
    public SpeedLimitChange ObserveLimit(float limitKph, bool hasLimit)
    {
        if (!hasLimit)
        {
            _lastLimitKph = null;
            return SpeedLimitChange.None;
        }

        if (_lastLimitKph is not { } last)
        {
            _lastLimitKph = limitKph;
            return SpeedLimitChange.None;
        }

        var delta = limitKph - last;
        if (Math.Abs(delta) < LimitChangeToleranceKph)
        {
            return SpeedLimitChange.None;
        }

        _lastLimitKph = limitKph;
        return delta > 0 ? SpeedLimitChange.Increased : SpeedLimitChange.Decreased;
    }

    /// <summary>
    /// Call every snapshot with the DISPLAYED speed and limit (same integers the speed colour uses).
    /// Returns true on the one tick the beep should sound: the moment the player crosses from inside
    /// the amber band to past it. A limit that drops under a player who was fine a moment ago counts
    /// as a crossing. Not eligible (paused, game not active) or no limit data returns false and
    /// forgets the baseline, so unpausing - or connecting while already speeding - never beeps; the
    /// player has to come back inside the band first.
    /// </summary>
    public bool ObserveSpeed(int speedUnits, int limitUnits, bool hasLimit, bool eligible, DateTime nowUtc)
    {
        if (!hasLimit || !eligible)
        {
            _speedBaselined = false;
            return false;
        }

        var over = speedUnits - limitUnits;

        if (!_speedBaselined)
        {
            _speedBaselined = true;
            _beepArmed = over <= AmberBandUnits;
            return false;
        }

        if (over > AmberBandUnits)
        {
            if (!_beepArmed)
            {
                return false;
            }

            _beepArmed = false;
            if (nowUtc - _lastBeepUtc < MinBeepInterval)
            {
                return false;
            }

            _lastBeepUtc = nowUtc;
            return true;
        }

        if (over <= AmberBandUnits - RearmMarginUnits)
        {
            _beepArmed = true;
        }

        return false;
    }
}
