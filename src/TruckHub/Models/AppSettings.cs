using System.Collections.Generic;

namespace TruckHub.Models;

/// <summary>
/// Per-gearbox slot-to-gear-number calibration, keyed in AppSettings by TelemetrySnapshot.GearboxFingerprint
/// so switching between a few regularly-driven trucks doesn't require recalibrating each time.
/// </summary>
public sealed class GearboxCalibration
{
    /// <summary>gearNumber = HShifterSlot - NormalOffset, when the splitter is off.</summary>
    public int NormalOffset { get; set; }

    /// <summary>gearNumber = HShifterSlot - SplitterOffset, when the splitter is on. Null if this
    /// gearbox has no splitter, or the splitter range hasn't been calibrated yet.</summary>
    public int? SplitterOffset { get; set; }
}

public sealed class AppSettings
{
    public bool UseMetric { get; set; } = false;
    public bool UseImperialWeight { get; set; } = false;
    public bool UseMetricFuel { get; set; } = false;
    public double WindowLeft { get; set; } = 80;
    public double WindowTop { get; set; } = 80;
    public double WindowWidth { get; set; } = 300;
    public double WindowHeight { get; set; } = 330;

    /// <summary>Overrides auto-detection when set. Null means "keep auto-detecting".</summary>
    public string? ManualEts2Path { get; set; }
    public string? ManualAtsPath { get; set; }

    /// <summary>Overrides mod-folder auto-detection (normally Documents\&lt;Game&gt;\mod) for the
    /// mod load-order scanner. Null means "keep auto-detecting" - only needed if a user has moved
    /// their mod folder somewhere nonstandard.</summary>
    public string? ManualEts2ModFolderPath { get; set; }
    public string? ManualAtsModFolderPath { get; set; }

    /// <summary>Keyed by GearboxFingerprint. See GearboxCalibration for what each entry means.</summary>
    public Dictionary<string, GearboxCalibration> GearboxCalibrations { get; set; } = new();

    // Per-component display toggles - all default on. Turning one off removes it from layout
    // entirely (the card reflows to fit whatever's left) rather than leaving blank space.
    public bool ShowSpeedSection { get; set; } = true;
    public bool ShowSpeedSign { get; set; } = true;
    public bool ShowCruiseControl { get; set; } = true;
    public bool ShowParkingBrake { get; set; } = true;
    public bool ShowLightsStatus { get; set; } = true;
    public bool ShowGearRpmSection { get; set; } = true;
    public bool ShowRoute { get; set; } = true;
    public bool ShowCargo { get; set; } = true;
    public bool ShowPayout { get; set; } = true;
    public bool ShowDistance { get; set; } = true;
    public bool ShowTimeLeft { get; set; } = true;
    public bool ShowFuelSection { get; set; } = true;
    public bool ShowFuelEconomy { get; set; } = true;
    public bool ShowTripFuelForecast { get; set; } = true;
    public bool ShowAdBlue { get; set; } = true;
    public bool ShowWarnings { get; set; } = true;
    public bool ShowGameClock { get; set; } = true;

    /// <summary>
    /// The game's rest-time telemetry field reads the same whether the fatigue simulation is off
    /// or just hasn't ticked down yet, so there's no reliable way to auto-detect it - the user has
    /// to tell us. Defaults off since most players don't run the fatigue simulation.
    /// </summary>
    public bool FatigueSimulationEnabled { get; set; } = false;
}
