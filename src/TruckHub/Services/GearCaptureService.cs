using System;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>Snapshot of the H-shifter state at the instant the calibration hotkey was pressed.</summary>
public sealed record GearCapture(uint HShifterSlot, bool SplitterHigh, string GearboxFingerprint, DateTime CapturedAt);

/// <summary>
/// Holds the most recent gear capture from the global calibration hotkey. Exists because ATS resets
/// its H-shifter state to neutral the instant the game window loses focus - reading live telemetry
/// once the user has already tabbed into the Settings window to type a gear number always sees that
/// reset, not the gear they were actually in. The hotkey captures the real value while the game still
/// has focus, so the Settings window can label it after the fact instead of reading a live value.
/// </summary>
public sealed class GearCaptureService
{
    public event Action? Changed;

    public GearCapture? Pending { get; private set; }

    public void Capture(TelemetrySnapshot snapshot)
    {
        if (!snapshot.SdkActive || !snapshot.IsHShifter)
        {
            return;
        }

        Pending = new GearCapture(snapshot.HShifterSlot, snapshot.SplitterHigh, snapshot.GearboxFingerprint, DateTime.Now);
        AppLogger.Log($"Gear capture: slot {snapshot.HShifterSlot}, splitter {(snapshot.SplitterHigh ? "on" : "off")}");
        Changed?.Invoke();
    }

    public void Clear()
    {
        Pending = null;
        Changed?.Invoke();
    }
}
