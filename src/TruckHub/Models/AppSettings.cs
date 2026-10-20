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
    public bool ShowOdometer { get; set; } = true;

    /// <summary>
    /// Recompute the GPS route automatically as soon as the truck is more than 200 m off it (how earlier
    /// versions behaved). Default OFF: stopping for fuel or a rest area must not rebuild a route, so instead an
    /// OFF ROUTE alert + Reroute button (and the Ctrl+Alt+F8 hotkey) appear once the truck is a mile of actual
    /// driving from the route, and the user decides when to recompute. Both distances are engine metres, which the
    /// odometer counts as several times more - see Services\OffRouteMonitor.cs (UNITS note).
    /// </summary>
    public bool AutoRerouteEnabled { get; set; } = false;

    // Rendering (Settings > App Configure). Both default OFF = everything draws on the GPU, as it always
    // has. They are independent and each takes effect on the next start. See Services\RenderSettings.cs.
    /// <summary>Draw the HUD windows (main dash, EXT gauges, ...) with the CPU - WPF software rendering - instead of the GPU.</summary>
    public bool HudSoftwareRendering { get; set; } = false;
    /// <summary>Run the GPS map's embedded browser without GPU acceleration, so the map draws on the CPU.</summary>
    public bool MapSoftwareRendering { get; set; } = false;

    // Speed alerts (main HUD) - audio cues layered on the existing speed sign/colours rather than new
    // display components. The two sounds are fully independent: each has its own on/off and its own
    // volume (0-100, 0 = silent, 50 = the default comfortable level, 100 = twice as loud), because the
    // limit-change beep fires far more often than the warning and wants to be set separately.
    /// <summary>One beep when the posted speed limit changes: higher tone = went up, lower tone = went down.</summary>
    public bool SpeedLimitChangeBeepEnabled { get; set; } = true;
    public int SpeedLimitChangeBeepVolume { get; set; } = 50;
    /// <summary>Quick double beep when the speed readout turns red (past the 5-over amber band).</summary>
    public bool OverLimitBeepEnabled { get; set; } = true;
    public int OverLimitBeepVolume { get; set; } = 50;

    // EXT window (extended instruments) display toggles - same "off removes it, reflows" convention
    // as the main dash's own toggles above.
    public bool ShowExtOilPressure { get; set; } = true;
    public bool ShowExtOilTemp { get; set; } = true;
    public bool ShowExtWaterTemp { get; set; } = true;
    public bool ShowExtBattery { get; set; } = true;
    public bool ShowExtBrakeAir { get; set; } = true;
    public bool ShowExtBrakeTemp { get; set; } = true;
    public bool ShowExtSuspension { get; set; } = true;
    public bool ShowExtTrailerAxleStatus { get; set; } = true;
    public bool ShowExtWear { get; set; } = true;

    /// <summary>
    /// The game's rest-time telemetry field reads the same whether the fatigue simulation is off
    /// or just hasn't ticked down yet, so there's no reliable way to auto-detect it - the user has
    /// to tell us. Defaults off since most players don't run the fatigue simulation.
    /// </summary>
    public bool FatigueSimulationEnabled { get; set; } = false;
}
