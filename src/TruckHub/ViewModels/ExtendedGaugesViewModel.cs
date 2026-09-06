using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using TruckHub.Models;
using TruckHub.Services;

namespace TruckHub.ViewModels;

/// <summary>One truck or trailer wheel's suspension reading, for the EXT tab's per-wheel bar list.</summary>
public sealed class WheelDeflection
{
    public string Label { get; init; } = "";
    /// <summary>0..1, scaled against an assumed max travel - see ExtendedGaugesViewModel's own
    /// AssumedMaxSuspDeflectionM comment for why this is approximate, not a precise limit. Forced to
    /// 0 for a raised lift axle - see IsLiftedAxle below.</summary>
    public double FillHeight { get; init; }
    public string DisplayValue { get; init; } = "";

    /// <summary>True when this wheel belongs to a liftable (tag/pusher) axle that's currently
    /// raised - a near-zero deflection reading there means "deliberately off the ground", not
    /// "flat/broken", so the label/value read "UP" in an accent color instead of a bogus mm figure.
    /// A liftable axle that's currently down looks exactly like any other axle - no permanent
    /// marking - matching how the real thing only stands out from the rest when it's actually
    /// raised.</summary>
    public bool IsLiftedAxle { get; init; }

    public string LabelColor => IsLiftedAxle ? "#FFC24C" : "#999999";
    public string ValueColor => IsLiftedAxle ? "#FFC24C" : "#DDDDDD";
}

/// <summary>One trailer wheel's physical lift/ground-contact state, for the axle-status list.</summary>
public sealed class TrailerAxleStatus
{
    public string Label { get; init; } = "";
    public string StateText { get; init; } = "";
    public string StateColor { get; init; } = "";
}

/// <summary>
/// Backs the EXT window - extended instrumentation the main dash doesn't surface (oil/water/battery/
/// brake gauges, accessory wear, suspension deflection, trailer axle status). Same shape as
/// GpsMapViewModel: its own small viewmodel subscribing directly to the shared TelemetryService,
/// rather than routing through MainViewModel, since this window is fully independent of the main HUD.
///
/// Uses a single blanket OnPropertyChanged(string.Empty) per snapshot instead of MainViewModel's
/// per-property NotifyIfChanged - that machinery exists there to avoid needless UI work on the
/// main HUD's tight, always-visible dials; this is a secondary diagnostics window opened
/// occasionally, so the simpler "just refresh everything" approach is a reasonable tradeoff.
/// </summary>
public sealed class ExtendedGaugesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TelemetryService _telemetryService;
    private readonly SettingsService _settingsService;
    private TelemetrySnapshot _snapshot;
    private AppSettings _settings;

    private const string WarningColorRed = "#FF4C4C";
    private const string WarningColorAmber = "#FFC24C";
    private const string WarningColorDim = "#555555";
    private const string GoodColor = "#5FD85F";
    private const string NeutralColor = "#FFFFFF";

    // Fixed gauge-face ranges: the SDK gives raw readings for these but, unlike fuel/AdBlue, no
    // per-truck "capacity" constant to stretch the dial to - these are reasonable real-world
    // heavy-truck gauge ranges, the same convention as any analog gauge's fixed printed scale.
    private const float OilPressureMaxPsi = 100f;
    private const float OilTempMinC = 40f, OilTempMaxC = 150f;
    private const float WaterTempMinC = 40f, WaterTempMaxC = 120f;
    private const float BrakeAirMaxPsi = 150f;

    // Dial headroom above the danger threshold below, so the needle has somewhere to go past red
    // rather than pegging exactly at the danger line.
    private const float BrakeTempMaxC = 450f;

    // Heavy-truck drum-brake reference points (SDK exposes no per-vehicle max, unlike EngineRpmMax for
    // RpmColor): normal service braking stays under ~260C; sustained heavy use (e.g. a long descent)
    // up to ~343C (650F) risks brake fade; sustained temps above that risk permanent drum/lining
    // damage. Converted from real-world commercial drum-brake guidance (150-400F normal service range,
    // 600-650F sustained ceiling before fade/damage) - see Marathon Brake Systems' brake-temperature
    // tech note and the HBN drum-brake operating-temperature reference.
    private const float BrakeTempCautionC = 260f;
    private const float BrakeTempDangerC = 343f;

    // No documented max suspension travel anywhere in the SDK - this is an approximation used only
    // to scale the bar fill for a relative "how loaded/compressed is this wheel" visual, not a
    // precise physical limit.
    private const float AssumedMaxSuspDeflectionM = 0.15f;

    public ExtendedGaugesViewModel(TelemetryService telemetryService, SettingsService settingsService)
    {
        _telemetryService = telemetryService;
        _settingsService = settingsService;
        _settings = settingsService.Load();
        _snapshot = telemetryService.LastSnapshot;
        _telemetryService.SnapshotUpdated += OnSnapshotUpdated;
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Re-reads settings from disk and refreshes bindings - call after the EXT settings
    /// window closes, since it edits its own in-memory copy of AppSettings, not this one. Same
    /// pattern as MainViewModel.ReloadSettings().</summary>
    public void ReloadSettings()
    {
        _settings = _settingsService.Load();
        OnPropertyChanged(string.Empty);
    }

    // Same "no info until a game's actually detected" behavior as the main window, rather than
    // showing a wall of zeroed-out/"--" gauges before anything's connected.
    public bool IsConnected => _snapshot.SdkActive;
    public bool IsDisconnected => !IsConnected;
    public string StatusMessage => IsConnected ? "" : "Waiting for ETS2 / ATS to start…";

    private static Visibility ToVisibility(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OilPressureVisibility => ToVisibility(_settings.ShowExtOilPressure);
    public Visibility OilTempVisibility => ToVisibility(_settings.ShowExtOilTemp);
    public Visibility WaterTempVisibility => ToVisibility(_settings.ShowExtWaterTemp);
    public Visibility BatteryVisibility => ToVisibility(_settings.ShowExtBattery);
    public Visibility BrakeAirVisibility => ToVisibility(_settings.ShowExtBrakeAir);
    public Visibility BrakeTempVisibility => ToVisibility(_settings.ShowExtBrakeTemp);
    public Visibility SuspensionVisibility => ToVisibility(_settings.ShowExtSuspension);
    public Visibility TrailerAxleStatusVisibility => ToVisibility(_settings.ShowExtTrailerAxleStatus);
    public Visibility WearVisibility => ToVisibility(_settings.ShowExtWear);

    // Real gauges are electrically driven and have no power of their own to hold a reading - same
    // reasoning as MainViewModel's FuelNeedleAngle - so every EXT gauge rests at "--"/0 when off.
    private bool Powered => _snapshot.ElectricEnabled;

    private static double NeedleAngle(double fraction) => -60.0 + Math.Clamp(fraction, 0.0, 1.0) * 120.0;

    public double OilPressureFraction => Powered ? Math.Clamp(_snapshot.OilPressurePsi / OilPressureMaxPsi, 0, 1) : 0;
    public double OilPressureNeedleAngle => NeedleAngle(OilPressureFraction);
    public string OilPressureDisplay => Powered ? $"{_snapshot.OilPressurePsi:0}" : "--";
    public string OilPressureColor => Powered && _snapshot.WarnOilPressure ? WarningColorRed : NeutralColor;

    public double OilTempFraction => Powered ? Math.Clamp((_snapshot.OilTemperatureC - OilTempMinC) / (OilTempMaxC - OilTempMinC), 0, 1) : 0;
    public double OilTempNeedleAngle => NeedleAngle(OilTempFraction);
    public string OilTempDisplay => Powered ? $"{_snapshot.OilTemperatureC:0}" : "--";
    public string OilTempColor => Powered && OilTempFraction > 0.85 ? WarningColorRed : Powered && OilTempFraction > 0.65 ? WarningColorAmber : Powered ? GoodColor : NeutralColor;

    public double WaterTempFraction => Powered ? Math.Clamp((_snapshot.WaterTemperatureC - WaterTempMinC) / (WaterTempMaxC - WaterTempMinC), 0, 1) : 0;
    public double WaterTempNeedleAngle => NeedleAngle(WaterTempFraction);
    public string WaterTempDisplay => Powered ? $"{_snapshot.WaterTemperatureC:0}" : "--";
    public string WaterTempColor => Powered && _snapshot.WarnWaterTemperature ? WarningColorRed : Powered ? GoodColor : NeutralColor;

    // Scaled around the game's own per-vehicle low-battery warning threshold rather than a fixed
    // number - ATS trucks run 12V electrical systems, ETS2 trucks run 24V, so a single hardcoded
    // range would peg a 12V ATS truck's needle at the low end permanently. Falls back to a plausible
    // 24V-system range if the SDK hasn't reported a threshold yet (e.g. the instant after connecting).
    private float BatteryVoltMin => _snapshot.BatteryVoltageWarnThreshold > 0.1f ? _snapshot.BatteryVoltageWarnThreshold * 0.85f : 18f;
    private float BatteryVoltMax => _snapshot.BatteryVoltageWarnThreshold > 0.1f ? _snapshot.BatteryVoltageWarnThreshold * 1.35f : 30f;

    public double BatteryFraction => Powered ? Math.Clamp((_snapshot.BatteryVoltage - BatteryVoltMin) / (BatteryVoltMax - BatteryVoltMin), 0, 1) : 0;
    public double BatteryNeedleAngle => NeedleAngle(BatteryFraction);
    public string BatteryDisplay => Powered ? $"{_snapshot.BatteryVoltage:0.0}" : "--";
    public string BatteryColor => Powered && _snapshot.WarnBatteryVoltage ? WarningColorRed : NeutralColor;

    public double BrakeAirFraction => Powered ? Math.Clamp(_snapshot.BrakeAirPressurePsi / BrakeAirMaxPsi, 0, 1) : 0;
    public double BrakeAirNeedleAngle => NeedleAngle(BrakeAirFraction);
    public string BrakeAirDisplay => Powered ? $"{_snapshot.BrakeAirPressurePsi:0}" : "--";
    public string BrakeAirColor => Powered && (_snapshot.WarnLowAirPressure || _snapshot.WarnAirPressureEmergency) ? WarningColorRed : NeutralColor;

    public double BrakeTempFraction => Powered ? Math.Clamp(_snapshot.BrakeTemperatureC / BrakeTempMaxC, 0, 1) : 0;
    public double BrakeTempNeedleAngle => NeedleAngle(BrakeTempFraction);
    public string BrakeTempDisplay => Powered ? $"{_snapshot.BrakeTemperatureC:0}" : "--";
    public string BrakeTempColor =>
        !Powered ? NeutralColor
        : _snapshot.BrakeTemperatureC >= BrakeTempDangerC ? WarningColorRed
        : _snapshot.BrakeTemperatureC >= BrakeTempCautionC ? WarningColorAmber
        : GoodColor;

    private static string WearColor(float wear) => wear > 0.8f ? WarningColorRed : wear > 0.5f ? WarningColorAmber : GoodColor;
    private static string WearPercent(float wear) => $"{wear * 100:0}%";

    public double EngineWearFraction => _snapshot.EngineWear;
    public string EngineWearDisplay => WearPercent(_snapshot.EngineWear);
    public string EngineWearColor => WearColor(_snapshot.EngineWear);

    public double TransmissionWearFraction => _snapshot.TransmissionWear;
    public string TransmissionWearDisplay => WearPercent(_snapshot.TransmissionWear);
    public string TransmissionWearColor => WearColor(_snapshot.TransmissionWear);

    public double CabinWearFraction => _snapshot.CabinWear;
    public string CabinWearDisplay => WearPercent(_snapshot.CabinWear);
    public string CabinWearColor => WearColor(_snapshot.CabinWear);

    public double ChassisWearFraction => _snapshot.ChassisWear;
    public string ChassisWearDisplay => WearPercent(_snapshot.ChassisWear);
    public string ChassisWearColor => WearColor(_snapshot.ChassisWear);

    public double TruckWheelsWearFraction => _snapshot.WheelsWearAvg;
    public string TruckWheelsWearDisplay => WearPercent(_snapshot.WheelsWearAvg);
    public string TruckWheelsWearColor => WearColor(_snapshot.WheelsWearAvg);

    public bool HasTrailer => _snapshot.TrailerAttached;
    public string TrailerHeaderText => _snapshot.TrailerAttached ? _snapshot.TrailerName : "No trailer attached";

    public double TrailerBodyWearFraction => _snapshot.TrailerBodyWear;
    public string TrailerBodyWearDisplay => WearPercent(_snapshot.TrailerBodyWear);
    public string TrailerBodyWearColor => WearColor(_snapshot.TrailerBodyWear);

    public double TrailerCargoWearFraction => _snapshot.TrailerCargoWear;
    public string TrailerCargoWearDisplay => WearPercent(_snapshot.TrailerCargoWear);
    public string TrailerCargoWearColor => WearColor(_snapshot.TrailerCargoWear);

    public double TrailerChassisWearFraction => _snapshot.TrailerChassisWear;
    public string TrailerChassisWearDisplay => WearPercent(_snapshot.TrailerChassisWear);
    public string TrailerChassisWearColor => WearColor(_snapshot.TrailerChassisWear);

    public double TrailerWheelsWearFraction => _snapshot.TrailerWheelsWear;
    public string TrailerWheelsWearDisplay => WearPercent(_snapshot.TrailerWheelsWear);
    public string TrailerWheelsWearColor => WearColor(_snapshot.TrailerWheelsWear);

    /// <summary>Truck suspension deflection, one bar per wheel.</summary>
    public IReadOnlyList<WheelDeflection> TruckWheelDeflections => BuildDeflections(
        _snapshot.TruckSuspDeflection, _snapshot.TruckWheelLiftable, _snapshot.TruckWheelLift,
        _snapshot.TruckWheelPowered, _snapshot.TruckWheelSteerable);

    // No Powered/Steerable arrays passed for the trailer - every trailer axle is inherently neither,
    // so that signal can't tell a "tag" axle apart from an ordinary one there (see
    // TruckWheelPowered/Steerable's own comment). A trailer axle is either liftable (labeled LIFT)
    // or just a plain numbered wheel.
    public IReadOnlyList<WheelDeflection> TrailerWheelDeflections => BuildDeflections(
        _snapshot.TrailerSuspDeflection, _snapshot.TrailerWheelLiftable, _snapshot.TrailerWheelLift,
        Array.Empty<bool>(), Array.Empty<bool>());

    private static IReadOnlyList<WheelDeflection> BuildDeflections(
        float[] deflection, bool[] liftable, float[] lift, bool[] powered, bool[] steerable)
    {
        if (deflection.Length == 0)
        {
            return Array.Empty<WheelDeflection>();
        }

        var list = new List<WheelDeflection>(deflection.Length);
        for (var i = 0; i < deflection.Length; i++)
        {
            var isLiftAxle = i < liftable.Length && liftable[i];
            var isLifted = isLiftAxle && i < lift.Length && lift[i] > 0.5f;
            // A fixed auxiliary axle - the SDK's own classification says it's neither driven nor
            // steered nor liftable, so it's an extra load-bearing "tag" axle, not an ordinary
            // steer/drive wheel worth just numbering.
            var isFixedTagAxle = !isLiftAxle && i < powered.Length && i < steerable.Length
                && !powered[i] && !steerable[i];
            var label = isLiftAxle ? "LIFT" : isFixedTagAxle ? "TAG" : $"W{i + 1}";
            var meters = Math.Abs(deflection[i]);
            list.Add(new WheelDeflection
            {
                Label = label,
                FillHeight = isLifted ? 0.0 : Math.Clamp(meters / AssumedMaxSuspDeflectionM, 0, 1),
                DisplayValue = isLifted ? "UP" : $"{meters * 1000:0}mm",
                IsLiftedAxle = isLifted,
            });
        }
        return list;
    }

    /// <summary>Trailer wheels' actual physical lift/ground-contact state - distinct from
    /// TrailerLiftAxleControlText below, which is the tractor's own single control/lamp for
    /// commanding the trailer's lift axle, not a per-wheel reading.</summary>
    public IReadOnlyList<TrailerAxleStatus> TrailerAxles => BuildAxleStatus();

    private IReadOnlyList<TrailerAxleStatus> BuildAxleStatus()
    {
        var lift = _snapshot.TrailerWheelLift;
        var ground = _snapshot.TrailerWheelOnGround;
        if (lift.Length == 0)
        {
            return Array.Empty<TrailerAxleStatus>();
        }

        var list = new List<TrailerAxleStatus>(lift.Length);
        for (var i = 0; i < lift.Length; i++)
        {
            var lifted = lift[i] > 0.5f;
            var onGround = i < ground.Length && ground[i];
            list.Add(new TrailerAxleStatus
            {
                Label = $"W{i + 1}",
                StateText = lifted ? "LIFTED" : onGround ? "DOWN" : "AIRBORNE",
                StateColor = lifted ? WarningColorAmber : onGround ? GoodColor : WarningColorRed,
            });
        }
        return list;
    }

    public string TrailerLiftAxleControlColor => _snapshot.TrailerLiftAxleIndicatorOn ? WarningColorAmber : WarningColorDim;
    public string TrailerLiftAxleControlText => _snapshot.TrailerLiftAxleUp ? "TRAILER LIFT AXLE: UP" : "TRAILER LIFT AXLE: DOWN";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _telemetryService.SnapshotUpdated -= OnSnapshotUpdated;
}
