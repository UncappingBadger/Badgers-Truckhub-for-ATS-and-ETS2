using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using TruckHub.Models;
using TruckHub.Services;

namespace TruckHub.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const double KmToMiles = 0.621371;
    private const double LitersToUsGallons = 0.264172;
    private const double TonnesToLbs = 2204.62;

    private const string GoodColor = "#5FD85F";
    private const string NeutralColor = "#FFFFFF";
    private const string BeamColor = "#4C9AFF";

    private readonly SettingsService _settingsService;
    private readonly TelemetryService _telemetryService;
    private AppSettings _settings;

    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Disconnected;
    private bool _autoUnitPicked;

    // FuelAverageConsumptionLPerKm is a live rolling average that can briefly read ~0 while coasting
    // or between gears - reacting to that instant-by-instant would flicker the trip-needs row off and
    // back on every time it dips, even though the "no data yet" case it's meant to guard against only
    // really happens once, right after loading a save. Stick with the last real reading instead.
    private float _lastKnownConsumptionLPerKm;

    // Distance traveled so far along the current job's route, tracked from our own baseline
    // (see OnSnapshotUpdated) rather than the job's quoted PlannedDistanceKm.
    private float? _initialNavigationDistanceMeters;

    public MainViewModel(SettingsService settingsService, TelemetryService telemetryService)
    {
        _settingsService = settingsService;
        _telemetryService = telemetryService;

        _autoUnitPicked = settingsService.HasSavedSettings;
        _settings = settingsService.Load();

        _telemetryService.SnapshotUpdated += OnSnapshotUpdated;
    }

    public bool UseMetric => _settings.UseMetric;

    public string UnitLabel => _settings.UseMetric ? "KM/H" : "MPH";
    public string DistanceUnitLabel => _settings.UseMetric ? "km" : "mi";
    public string WeightUnitLabel => _settings.UseImperialWeight ? "LBS" : "T";
    public string FuelUnitLabel => _settings.UseMetricFuel ? "L" : "GAL";

    public bool IsConnected => _snapshot.SdkActive;

    public string StatusMessage => _snapshot switch
    {
        { SdkActive: false } => "Waiting for ETS2 / ATS to start…",
        { Paused: true } => "Paused",
        { OnJob: false } => "No active job",
        _ => "",
    };

    public bool ShowJobPanel => _snapshot.SdkActive && _snapshot.OnJob;
    public bool ShowStatusMessage => !ShowJobPanel;

    public int SpeedLimitValue => ConvertSpeed(_snapshot.SpeedLimitKph);

    public bool HasSpeedLimit => _snapshot.SdkActive && _snapshot.SpeedLimitKph > 0.5f;
    public bool ShowSpeedPlaceholder => !HasSpeedLimit;

    // ETS2's European roads use round red-ringed signs, not the American rectangle - matches
    // real road sign conventions for whichever game is actually running.
    public bool ShowAmericanSpeedSign => HasSpeedLimit && _snapshot.Game != SimGame.Ets2;
    public bool ShowEuropeanSpeedSign => HasSpeedLimit && _snapshot.Game == SimGame.Ets2;

    public int SpeedValue => ConvertSpeed(Math.Abs(_snapshot.SpeedKph));

    // Green at/under the limit, amber up to 5 over, red beyond that - no limit data means nothing
    // to compare against, so stay neutral rather than implying "you're fine" or "you're speeding".
    public string SpeedColor
    {
        get
        {
            if (!HasSpeedLimit)
            {
                return NeutralColor;
            }

            var over = SpeedValue - SpeedLimitValue;
            if (over <= 0) return GoodColor;
            return over <= 5 ? WarningColorAmber : WarningColorRed;
        }
    }

    // Fill fraction against a fixed reference max matching a typical truck speedometer's dial
    // (there's no "truck's real top speed" telemetry to compare against instead).
    public string SpeedRingArcData
    {
        get
        {
            var referenceMax = _settings.UseMetric ? 140.0 : 90.0;
            return BuildRingArcData(SpeedValue / referenceMax);
        }
    }

    public bool CruiseControlActive => _snapshot.SdkActive && _snapshot.CruiseControlOn;

    // Shown regardless of on/off (dashboards typically keep showing the set speed while
    // disengaged too) - the icon/text color is what actually signals on vs off.
    public string CruiseControlDisplay => $"{ConvertSpeed(_snapshot.CruiseControlSpeedKph)} {UnitLabel}";

    public string CruiseControlColor => CruiseControlActive ? "#5FD85F" : "#555555";

    // Real dashboards show the parking brake telltale red regardless of whether it's a deliberate
    // manual engagement or an automatic emergency application - there isn't a separate colour
    // convention for "this one's not a big deal", so this matches the emergency-brake telltale in
    // the warning cluster despite representing a different underlying condition.
    public bool ParkingBrakeActive => _snapshot.SdkActive && _snapshot.ParkingBrakeOn;

    public string ParkingBrakeColor => ParkingBrakeActive ? WarningColorRed : WarningColorDim;

    // Green arrows, lit straight off the game's own already-blinking bulb state - hazards flash
    // both together because they drive the same two fields, so no separate hazard handling needed.
    public string TurnSignalLeftColor => _snapshot.TurnSignalLeftOn ? GoodColor : WarningColorDim;
    public string TurnSignalRightColor => _snapshot.TurnSignalRightOn ? GoodColor : WarningColorDim;

    // Real dashboard telltale colors: blue is the near-universal high-beam indicator; sidelights and
    // the roof beacon are conventionally amber; low beam gets green as a simple "lights on and safe" cue.
    public string SidelightsColor => _snapshot.SidelightsOn ? GoodColor : WarningColorDim;
    public string LowBeamColor => _snapshot.LowBeamOn ? GoodColor : WarningColorDim;
    public string HighBeamColor => _snapshot.HighBeamOn ? BeamColor : WarningColorDim;
    public string BeaconColor => _snapshot.BeaconOn ? WarningColorAmber : WarningColorDim;

    // Amber, matching real diff-lock/4WD-low style indicators - it changes handling characteristics,
    // so it's treated as "worth noticing" rather than plain neutral white.
    public string DiffLockColor => _snapshot.DifferentialLockOn ? WarningColorAmber : WarningColorDim;

    // Only shown at all if this specific truck actually has a liftable axle (see HasLiftAxle).
    // "Down" (full axle load - the normal running state) is dimmed like everything else's default;
    // "up" changes load distribution and handling (and often carries legal speed/weight conditions),
    // so it gets the same amber "worth noticing" treatment as diff lock rather than plain white.
    public bool HasLiftAxle => _snapshot.HasLiftAxle;
    public string LiftAxleColor => _snapshot.LiftAxleUp ? WarningColorAmber : WarningColorDim;

    public string CurrencySymbol => _snapshot.Game == SimGame.Ats ? "$" : "€";

    public string IncomeDisplay => $"{CurrencySymbol}{_snapshot.Income:N0}";

    public string RouteDisplay => (_snapshot.CitySource, _snapshot.CityDestination) switch
    {
        ("", "") => "",
        var (s, d) => $"{FormatStop(_snapshot.CompanySource, s)} → {FormatStop(_snapshot.CompanyDestination, d)}",
    };

    private static string FormatStop(string company, string city) =>
        string.IsNullOrWhiteSpace(company) ? city : $"{company}, {city}";

    // Counts up from 0 as the job progresses (distance driven so far), not down from the total.
    // Uses our own captured baseline (see OnSnapshotUpdated), not the job's quoted PlannedDistanceKm
    // - a ferry leg, a detour, or any route recalculation can make the GPS's actual route distance
    // diverge a lot from the job board's quoted estimate, and subtracting numbers on two different
    // bases either undercounts progress or clamps to 0 km until they happen to cross.
    public string DistanceTraveledDisplay
    {
        get
        {
            var baselineMeters = _initialNavigationDistanceMeters ?? _snapshot.NavigationDistanceMeters;
            var traveledKm = (baselineMeters - _snapshot.NavigationDistanceMeters) / 1000.0;
            return FormatDistance(Math.Max(0, traveledKm));
        }
    }

    // Our own captured baseline, not PlannedDistanceKm - see DistanceTraveledDisplay. Keeps the two
    // figures self-consistent (always reaches exactly 100% at delivery) even when it disagrees with
    // the job board's quoted distance.
    public string DistanceTotalDisplay =>
        FormatDistance((_initialNavigationDistanceMeters ?? _snapshot.NavigationDistanceMeters) / 1000.0);

    public bool HasCargo => !string.IsNullOrWhiteSpace(_snapshot.CargoName);

    public string CargoDisplay
    {
        get
        {
            if (!HasCargo)
            {
                return "";
            }

            var tonnes = _snapshot.CargoMassKg / 1000.0;
            return _settings.UseImperialWeight
                ? $"{_snapshot.CargoName} {tonnes * TonnesToLbs:N0}LBS"
                : $"{_snapshot.CargoName} {tonnes:0}T";
        }
    }

    public string FuelDisplay
    {
        get
        {
            var value = _settings.UseMetricFuel ? _snapshot.FuelLiters : _snapshot.FuelLiters * LitersToUsGallons;
            var unit = _settings.UseMetricFuel ? "L" : "gal";
            var text = $"{value:0} {unit}";

            if (_snapshot.FuelCapacityLiters > 0.1f)
            {
                var percent = _snapshot.FuelLiters / _snapshot.FuelCapacityLiters * 100.0;
                text += $" ({percent:0}%)";
            }

            return text;
        }
    }

    // Same staleness guard as the forecast below - _lastKnownConsumptionLPerKm rather than the raw
    // live reading, which can flicker to ~0 while coasting.
    public bool HasFuelEconomy => _lastKnownConsumptionLPerKm > 0.0001f;

    // Tied to the distance/speed unit (UseMetric), not the fuel volume unit - MPG is meaningless
    // without a matching distance unit (miles), and L/100km without km, regardless of whether the
    // driver prefers to see the raw fuel amount above in liters or gallons.
    public string FuelEconomyDisplay
    {
        get
        {
            if (_settings.UseMetric)
            {
                return $"{_lastKnownConsumptionLPerKm * 100.0:0.0} L/100km";
            }

            var milesPerGallon = KmToMiles / (_lastKnownConsumptionLPerKm * LitersToUsGallons);
            return $"{milesPerGallon:0.0} MPG";
        }
    }

    // Average consumption only becomes meaningful once the truck has actually driven a bit
    // (it reads 0 right after loading a save), so don't show a misleading "0% needed" before then.
    public bool HasFuelForecast =>
        ShowJobPanel && _snapshot.FuelCapacityLiters > 0.1f && _lastKnownConsumptionLPerKm > 0.0001f;

    public string FuelForecastDisplay
    {
        get
        {
            var remainingKm = _snapshot.NavigationDistanceMeters / 1000.0;
            var neededLiters = remainingKm * _lastKnownConsumptionLPerKm;
            var neededPercent = neededLiters / _snapshot.FuelCapacityLiters * 100.0;
            return $"{neededPercent:0}%";
        }
    }

    public bool HasEnoughFuelForTrip
    {
        get
        {
            var remainingKm = _snapshot.NavigationDistanceMeters / 1000.0;
            var neededLiters = remainingKm * _lastKnownConsumptionLPerKm;
            return _snapshot.FuelLiters >= neededLiters;
        }
    }

    public string TripFuelColor => HasEnoughFuelForTrip ? "#5FD85F" : "#FF6B6B";

    // Same format as FuelDisplay (amount + %), no trip forecast - AdBlue isn't part of the
    // per-trip consumption estimate the way fuel is.
    public bool HasAdBlue => _snapshot.AdBlueCapacityLiters > 0.1f;

    public string AdBlueDisplay
    {
        get
        {
            var value = _settings.UseMetricFuel ? _snapshot.AdBlueLiters : _snapshot.AdBlueLiters * LitersToUsGallons;
            var unit = _settings.UseMetricFuel ? "L" : "gal";
            var text = $"{value:0} {unit}";

            if (_snapshot.AdBlueCapacityLiters > 0.1f)
            {
                var percent = _snapshot.AdBlueLiters / _snapshot.AdBlueCapacityLiters * 100.0;
                text += $" ({percent:0}%)";
            }

            return text;
        }
    }

    private const string WarningColorRed = "#FF4C4C";
    private const string WarningColorAmber = "#FFC24C";
    private const string WarningColorDim = "#555555";

    // Mirrors the exact dashboard warning lights ATS/ETS2 already computes - no thresholds of our
    // own to get wrong. Rendered as individual telltales (like a real instrument cluster), each
    // dimmed grey until its own condition trips. Colors follow real vehicle dashboard convention:
    // red for anything safety-critical (brakes, oil pressure, overheating, charging system failure),
    // amber for anything that just needs attention soon (fuel, AdBlue).
    public string AirPressureWarnColor => _snapshot.WarnLowAirPressure ? WarningColorRed : WarningColorDim;
    public string ParkingBrakeWarnColor => _snapshot.WarnAirPressureEmergency ? WarningColorRed : WarningColorDim;
    public string OilPressureWarnColor => _snapshot.WarnOilPressure ? WarningColorRed : WarningColorDim;
    public string WaterTempWarnColor => _snapshot.WarnWaterTemperature ? WarningColorRed : WarningColorDim;
    public string FuelWarnColor => _snapshot.WarnLowFuel ? WarningColorAmber : WarningColorDim;
    public string AdBlueWarnColor => _snapshot.WarnLowAdBlue ? WarningColorAmber : WarningColorDim;
    // A charging-system failure is treated as critical on almost every real dashboard (you can lose
    // electrical power entirely) - red, not amber, matching that convention.
    public string BatteryWarnColor => _snapshot.WarnBatteryVoltage ? WarningColorRed : WarningColorDim;

    // Looks up the current gearbox's calibration (if any) by its fingerprint. Different gearboxes -
    // different trucks, or the same truck with a different transmission option - can reuse H-shifter
    // slots in completely different arrangements (e.g. reverse where this one has its crawler), so
    // each fingerprint gets its own remembered offset(s) rather than one global assumption.
    private GearboxCalibration? CurrentCalibration =>
        _snapshot.IsHShifter && !string.IsNullOrEmpty(_snapshot.GearboxFingerprint)
            ? _settings.GearboxCalibrations.GetValueOrDefault(_snapshot.GearboxFingerprint)
            : null;

    public string GearDisplay
    {
        get
        {
            var raw = _snapshot.GearDashboardsRaw;

            if (raw < 0)
            {
                return AppendRange($"R{Math.Abs(raw)}");
            }

            if (raw == 0)
            {
                return "N";
            }

            if (!_snapshot.IsHShifter)
            {
                // Automatic or simple sequential shifter - GearDashboards already is the real gear number.
                return raw.ToString();
            }

            var calibration = CurrentCalibration;
            var offset = calibration is null
                ? (int?)null
                : _snapshot.SplitterHigh ? calibration.SplitterOffset : calibration.NormalOffset;

            if (offset.HasValue)
            {
                var slot = (int)_snapshot.HShifterSlot;
                var gearNumber = slot - offset.Value;

                // Forward gear, but below where this truck's "normal" numbering starts -
                // e.g. a crawler gear sitting below 1st. Avoid showing 0, which reads as neutral.
                var core = gearNumber > 0 ? gearNumber.ToString() : "L";
                return AppendRange(core);
            }

            // Not calibrated (or calibrated only for the other splitter state) - that offset is
            // specific to one physical slot layout, so show the raw shifter slot instead of a
            // confidently wrong number. Calibrate this gearbox from Settings to fix.
            return AppendRange($"S{_snapshot.HShifterSlot}");
        }
    }

    private string AppendRange(string core) =>
        _snapshot.IsHShifter && _snapshot.RangeIsHigh.HasValue
            ? core + (_snapshot.RangeIsHigh.Value ? "H" : "L")
            : core;

    public string RpmDisplay => $"{_snapshot.Rpm:0}";

    // Green in normal range, amber approaching redline, red at/near it - thresholds are fractions
    // of the truck's actual redline (EngineRpmMax), not a fixed RPM number, so it's correct across
    // every engine.
    public string RpmColor
    {
        get
        {
            if (_snapshot.EngineRpmMax <= 0.1f)
            {
                return NeutralColor;
            }

            var fraction = _snapshot.Rpm / _snapshot.EngineRpmMax;
            if (fraction >= 0.9) return WarningColorRed;
            return fraction >= 0.75 ? WarningColorAmber : GoodColor;
        }
    }

    public string RpmRingArcData
    {
        get
        {
            var referenceMax = _snapshot.EngineRpmMax > 0.1f ? _snapshot.EngineRpmMax : 2500f;
            return BuildRingArcData(_snapshot.Rpm / referenceMax);
        }
    }

    public string RemainingTimeDisplay => FormatMinutes(_snapshot.RemainingDeliveryMinutes, showLatePrefix: true);

    private static string FormatMinutes(int minutes, bool showLatePrefix)
    {
        var late = minutes < 0;
        var absMinutes = Math.Abs(minutes);
        var hours = absMinutes / 60;
        var mins = absMinutes % 60;
        var core = hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
        return late && showLatePrefix ? $"Late {core}" : core;
    }

    public string PricePerDistanceDisplay
    {
        get
        {
            var totalDistance = _settings.UseMetric
                ? _snapshot.PlannedDistanceKm
                : _snapshot.PlannedDistanceKm * KmToMiles;

            if (totalDistance < 0.1)
            {
                return $"{CurrencySymbol}—";
            }

            var perDistance = _snapshot.Income / totalDistance;
            return $"{CurrencySymbol}{perDistance:0.00}/{DistanceUnitLabel}";
        }
    }

    // Component visibility: settings-driven (user turned it off) always collapses - that's a rare,
    // deliberate choice, and the card should reflow to fit whatever's left. Live-data-driven (waiting
    // for a connection, no cargo this leg, etc.) uses Hidden instead - that changes constantly during
    // normal play, and collapsing would make the whole card visibly rescale (it sits in a Viewbox)
    // every time, which reads as the window "resizing" on its own.
    public Visibility SpeedSectionVisibility =>
        !_settings.ShowSpeedSection ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    public Visibility CruiseControlVisibility =>
        !_settings.ShowCruiseControl ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    public Visibility ParkingBrakeVisibility =>
        !_settings.ShowParkingBrake ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    public Visibility LightsStatusVisibility =>
        !_settings.ShowLightsStatus ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    public Visibility GearRpmSectionVisibility =>
        !_settings.ShowGearRpmSection ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    public Visibility RouteVisibility =>
        _settings.ShowRoute ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CargoVisibility =>
        !_settings.ShowCargo ? Visibility.Collapsed
        : HasCargo ? Visibility.Visible : Visibility.Hidden;

    public Visibility PayoutVisibility =>
        _settings.ShowPayout ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DistanceVisibility =>
        _settings.ShowDistance ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TimeLeftVisibility =>
        _settings.ShowTimeLeft ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FuelSectionVisibility =>
        !_settings.ShowFuelSection ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    public Visibility FuelEconomyVisibility =>
        !_settings.ShowFuelEconomy ? Visibility.Collapsed
        : HasFuelEconomy ? Visibility.Visible : Visibility.Hidden;

    public Visibility TripFuelForecastVisibility =>
        !_settings.ShowTripFuelForecast ? Visibility.Collapsed
        : HasFuelForecast ? Visibility.Visible : Visibility.Hidden;

    public Visibility AdBlueVisibility =>
        !_settings.ShowAdBlue ? Visibility.Collapsed
        : IsConnected && HasAdBlue ? Visibility.Visible : Visibility.Hidden;

    public Visibility WarningsVisibility =>
        !_settings.ShowWarnings ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    private int ConvertSpeed(float kph) =>
        (int)Math.Round(_settings.UseMetric ? kph : kph * KmToMiles);

    // Path data for a ring-gauge fill arc, starting at 12 o'clock and sweeping clockwise by
    // `fraction` of a full turn. A 70x70 box (radius 31, centered at 35,35) matches the ring boxes
    // in MainWindow.xaml. Clamped just under 1.0 - an ArcSegment whose start and end point coincide
    // (an exact full circle) doesn't render reliably.
    private static string BuildRingArcData(double fraction)
    {
        const double radius = 31.0;
        const double center = 35.0;

        fraction = Math.Clamp(fraction, 0.0, 0.999);
        if (fraction <= 0.0005)
        {
            return "";
        }

        var angle = fraction * 2 * Math.PI;
        var startX = center;
        var startY = center - radius;
        var endX = center + radius * Math.Sin(angle);
        var endY = center - radius * Math.Cos(angle);
        var largeArc = fraction > 0.5 ? 1 : 0;

        return FormattableString.Invariant(
            $"M {startX:F2},{startY:F2} A {radius:F2},{radius:F2} 0 {largeArc},1 {endX:F2},{endY:F2}");
    }

    private string FormatDistance(double km)
    {
        var value = _settings.UseMetric ? km : km * KmToMiles;
        return $"{value:N0} {DistanceUnitLabel}";
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        var wasOnJob = _snapshot.OnJob;
        _snapshot = snapshot;

        // Track a running maximum of NavigationDistance as our "how far along the route" baseline,
        // rather than a one-time snapshot at job start or the job's quoted PlannedDistanceKm. A
        // one-time snapshot can still get stuck at 0 km traveled if the GPS hadn't finished
        // computing the route on the exact tick OnJob flipped true (baseline captured as 0 or
        // otherwise too low), or if the route gets recalculated *longer* mid-trip (a diversion, a
        // border crossing) - either way, current would end up exceeding a fixed baseline and the
        // subtraction clamps to 0 forever after. Letting the baseline ratchet upward whenever
        // current exceeds it self-heals from both cases instead of latching onto one bad reading.
        if (snapshot.OnJob)
        {
            if (!wasOnJob || _initialNavigationDistanceMeters is not { } baseline
                          || snapshot.NavigationDistanceMeters > baseline)
            {
                _initialNavigationDistanceMeters = snapshot.NavigationDistanceMeters;
            }
        }
        else
        {
            _initialNavigationDistanceMeters = null;
        }

        if (snapshot.FuelAverageConsumptionLPerKm > 0.0001f)
        {
            _lastKnownConsumptionLPerKm = snapshot.FuelAverageConsumptionLPerKm;
        }

        // Don't let a stale reading from a finished job/previous truck bleed into a new one.
        if (!snapshot.OnJob)
        {
            _lastKnownConsumptionLPerKm = 0f;
        }

        // First time we ever see a game connect with no saved preference, default the unit
        // to what that game's home market uses (ATS -> mph, ETS2 -> km/h) instead of guessing.
        if (!_autoUnitPicked && snapshot.SdkActive && snapshot.Game != SimGame.Unknown)
        {
            _settings.UseMetric = snapshot.Game == SimGame.Ets2;
            _settings.UseMetricFuel = snapshot.Game == SimGame.Ets2;
            _autoUnitPicked = true;
            _settingsService.Save(_settings);
        }

        RaiseAllDisplayPropertiesChanged();
    }

    /// <summary>Re-reads settings from disk and refreshes bindings - call after the Settings window
    /// closes, since it edits its own in-memory copy of AppSettings, not this one.</summary>
    public void ReloadSettings()
    {
        _settings = _settingsService.Load();
        // Unit toggles now live in Settings, not a title-bar button - once the user's been in
        // there, stop auto-picking the unit from the game's home market on the next connect.
        _autoUnitPicked = true;
        RaiseAllDisplayPropertiesChanged();
    }

    private void RaiseAllDisplayPropertiesChanged()
    {
        OnPropertyChanged(nameof(UnitLabel));
        OnPropertyChanged(nameof(DistanceUnitLabel));
        OnPropertyChanged(nameof(FuelUnitLabel));
        OnPropertyChanged(nameof(WeightUnitLabel));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ShowJobPanel));
        OnPropertyChanged(nameof(ShowStatusMessage));
        OnPropertyChanged(nameof(SpeedLimitValue));
        OnPropertyChanged(nameof(HasSpeedLimit));
        OnPropertyChanged(nameof(ShowSpeedPlaceholder));
        OnPropertyChanged(nameof(ShowAmericanSpeedSign));
        OnPropertyChanged(nameof(ShowEuropeanSpeedSign));
        OnPropertyChanged(nameof(SpeedValue));
        OnPropertyChanged(nameof(CurrencySymbol));
        OnPropertyChanged(nameof(IncomeDisplay));
        OnPropertyChanged(nameof(RouteDisplay));
        OnPropertyChanged(nameof(DistanceTraveledDisplay));
        OnPropertyChanged(nameof(DistanceTotalDisplay));
        OnPropertyChanged(nameof(PricePerDistanceDisplay));
        OnPropertyChanged(nameof(HasCargo));
        OnPropertyChanged(nameof(CargoDisplay));
        OnPropertyChanged(nameof(FuelDisplay));
        OnPropertyChanged(nameof(HasFuelEconomy));
        OnPropertyChanged(nameof(FuelEconomyDisplay));
        OnPropertyChanged(nameof(FuelEconomyVisibility));
        OnPropertyChanged(nameof(HasFuelForecast));
        OnPropertyChanged(nameof(FuelForecastDisplay));
        OnPropertyChanged(nameof(HasEnoughFuelForTrip));
        OnPropertyChanged(nameof(TripFuelColor));
        OnPropertyChanged(nameof(RemainingTimeDisplay));
        OnPropertyChanged(nameof(GearDisplay));
        OnPropertyChanged(nameof(RpmDisplay));
        OnPropertyChanged(nameof(RpmColor));
        OnPropertyChanged(nameof(RpmRingArcData));
        OnPropertyChanged(nameof(SpeedColor));
        OnPropertyChanged(nameof(SpeedRingArcData));
        OnPropertyChanged(nameof(SpeedSectionVisibility));
        OnPropertyChanged(nameof(CruiseControlActive));
        OnPropertyChanged(nameof(CruiseControlDisplay));
        OnPropertyChanged(nameof(CruiseControlColor));
        OnPropertyChanged(nameof(CruiseControlVisibility));
        OnPropertyChanged(nameof(ParkingBrakeActive));
        OnPropertyChanged(nameof(ParkingBrakeColor));
        OnPropertyChanged(nameof(ParkingBrakeVisibility));
        OnPropertyChanged(nameof(TurnSignalLeftColor));
        OnPropertyChanged(nameof(TurnSignalRightColor));
        OnPropertyChanged(nameof(SidelightsColor));
        OnPropertyChanged(nameof(LowBeamColor));
        OnPropertyChanged(nameof(HighBeamColor));
        OnPropertyChanged(nameof(BeaconColor));
        OnPropertyChanged(nameof(DiffLockColor));
        OnPropertyChanged(nameof(HasLiftAxle));
        OnPropertyChanged(nameof(LiftAxleColor));
        OnPropertyChanged(nameof(LightsStatusVisibility));
        OnPropertyChanged(nameof(GearRpmSectionVisibility));
        OnPropertyChanged(nameof(RouteVisibility));
        OnPropertyChanged(nameof(CargoVisibility));
        OnPropertyChanged(nameof(PayoutVisibility));
        OnPropertyChanged(nameof(DistanceVisibility));
        OnPropertyChanged(nameof(TimeLeftVisibility));
        OnPropertyChanged(nameof(FuelSectionVisibility));
        OnPropertyChanged(nameof(TripFuelForecastVisibility));
        OnPropertyChanged(nameof(HasAdBlue));
        OnPropertyChanged(nameof(AdBlueDisplay));
        OnPropertyChanged(nameof(AdBlueVisibility));
        OnPropertyChanged(nameof(AirPressureWarnColor));
        OnPropertyChanged(nameof(ParkingBrakeWarnColor));
        OnPropertyChanged(nameof(OilPressureWarnColor));
        OnPropertyChanged(nameof(WaterTempWarnColor));
        OnPropertyChanged(nameof(FuelWarnColor));
        OnPropertyChanged(nameof(AdBlueWarnColor));
        OnPropertyChanged(nameof(BatteryWarnColor));
        OnPropertyChanged(nameof(WarningsVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _telemetryService.SnapshotUpdated -= OnSnapshotUpdated;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
}
