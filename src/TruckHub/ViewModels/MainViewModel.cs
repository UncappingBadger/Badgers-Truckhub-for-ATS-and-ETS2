using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly UpdateCheckService _updateCheckService;
    private AppSettings _settings;

    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Disconnected;
    private bool _autoUnitPicked;
    private bool _updateAcknowledged;

    // The H-shifter's Low/High range switch reads live the instant it's physically flipped, but the
    // truck doesn't actually change range until the next full gear change completes - so displaying
    // the raw switch state directly showed "H" while the truck was still mechanically in L. Instead,
    // the displayed range only re-syncs to the switch's current position when GearDashboardsRaw
    // actually changes (a real shift just completed), and stays frozen at its last real value the
    // rest of the time. Confirmed live: flipping the switch alone never moved GearDashboardsRaw;
    // only an actual gear change did. Reset on disconnect/gearbox-fingerprint-change so a stale
    // frozen value from one truck never leaks into a different one.
    private bool? _displayedRangeIsHigh;
    private int? _lastGearDashboardsRawForRange;
    private string _lastGearboxFingerprintForRange = "";

    // FuelAverageConsumptionLPerKm is a live rolling average that can briefly read ~0 while coasting
    // or between gears - reacting to that instant-by-instant would flicker the trip-needs row off and
    // back on every time it dips, even though the "no data yet" case it's meant to guard against only
    // really happens once, right after loading a save. Stick with the last real reading instead.
    private float _lastKnownConsumptionLPerKm;

    // Distance traveled so far along the current job's route, tracked from our own baseline
    // (see OnSnapshotUpdated) rather than the job's quoted PlannedDistanceKm.
    private float? _initialNavigationDistanceMeters;

    // Fuel/AdBlue needle-gauge animation state - see FuelNeedleAngle/AdBlueLitSegments and
    // OnGaugeAnimationTick. Ticks independently of the telemetry poll rate so the sweep looks smooth
    // regardless of how often the game actually pushes new data.
    private double _fuelFractionSmoothed;
    private double _adBlueFractionSmoothed;
    private double _speedFractionSmoothed;
    private double _rpmFractionSmoothed;
    private readonly DispatcherTimer _gaugeAnimationTimer;

    // Session job logbook (v1.1) - never persisted, cleared when TruckHub closes. _pendingLogEntry is
    // rebuilt every tick a job is active (see OnSnapshotUpdated) so it always holds the latest known-
    // good state; TelemetryService.JobDelivered just commits whatever's cached at that point, rather
    // than trying to read fresh telemetry at the exact instant the event fires (which may already
    // reflect the "no job" state by then). JobCancelled discards it instead of committing it.
    public ObservableCollection<LogEntry> JobLog { get; } = new();
    private LogEntry? _pendingLogEntry;

    public bool HasLogEntries => JobLog.Count > 0;
    public bool HasNoLogEntries => JobLog.Count == 0;
    public string TotalPayoutDisplay => $"{CurrencySymbol}{JobLog.Sum(e => e.Income):N0}";
    public string TotalMileageDisplay => FormatDistance(JobLog.Sum(e => e.DistanceKm));

    public MainViewModel(SettingsService settingsService, TelemetryService telemetryService, UpdateCheckService updateCheckService)
    {
        _settingsService = settingsService;
        _telemetryService = telemetryService;
        _updateCheckService = updateCheckService;

        _autoUnitPicked = settingsService.HasSavedSettings;
        _settings = settingsService.Load();

        _telemetryService.SnapshotUpdated += OnSnapshotUpdated;
        _telemetryService.JobDelivered += OnJobDelivered;
        _telemetryService.JobCancelled += OnJobCancelled;
        _updateCheckService.Checked += OnUpdateChecked;

        _gaugeAnimationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _gaugeAnimationTimer.Tick += OnGaugeAnimationTick;
        _gaugeAnimationTimer.Start();

        // Silent by design - a failed/offline check should never surface to the user, only a genuine
        // update being found should (via UpdateAvailable, a small badge on the Settings button rather
        // than a popup). SettingsViewModel's manual "Check for Updates" button is the one place that
        // shows explicit feedback for a check the user actually asked for.
        _ = _updateCheckService.CheckAsync();
    }

    public bool UpdateAvailable => _updateCheckService.LastResult?.UpdateAvailable ?? false;

    // Once the user has opened Settings (where the update is shown in full, with a download link),
    // the badge on the main window has done its job - stop drawing attention back to it every launch
    // until a future check finds something newer than what they already acknowledged.
    public bool ShowUpdateIndicator => UpdateAvailable && !_updateAcknowledged;

    // Amber matches the rest of the HUD's "needs your attention" convention (warning lights, low
    // fuel/AdBlue, near-empty rest timer) rather than inventing a separate "update" colour.
    public string SettingsButtonColor => ShowUpdateIndicator ? WarningColorAmber : "White";

    public string SettingsButtonTooltip => ShowUpdateIndicator ? "Update available - Settings" : "Settings";

    public void AcknowledgeUpdate()
    {
        if (_updateAcknowledged)
        {
            return;
        }

        _updateAcknowledged = true;
        OnPropertyChanged(nameof(ShowUpdateIndicator));
        OnPropertyChanged(nameof(SettingsButtonColor));
        OnPropertyChanged(nameof(SettingsButtonTooltip));
    }

    private void OnUpdateChecked(UpdateCheckResult result)
    {
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(ShowUpdateIndicator));
        OnPropertyChanged(nameof(SettingsButtonColor));
        OnPropertyChanged(nameof(SettingsButtonTooltip));
    }

    private void OnJobDelivered()
    {
        if (_pendingLogEntry is not { } entry)
        {
            return;
        }

        _pendingLogEntry = null;
        JobLog.Insert(0, entry);
        OnPropertyChanged(nameof(HasLogEntries));
        OnPropertyChanged(nameof(HasNoLogEntries));
        OnPropertyChanged(nameof(TotalPayoutDisplay));
        OnPropertyChanged(nameof(TotalMileageDisplay));
    }

    private void OnJobCancelled() => _pendingLogEntry = null;

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

    // Reference max matching a typical truck speedometer's dial (there's no "truck's real top speed"
    // telemetry to size the dial to instead) - same fixed value the old ring gauge already used.
    private double SpeedGaugeReferenceMax => _settings.UseMetric ? 140.0 : 90.0;

    public string SpeedGaugeMidLabel => $"{SpeedGaugeReferenceMax / 2:0}";
    public string SpeedGaugeMaxLabel => $"{SpeedGaugeReferenceMax:0}";

    // See FuelNeedleAngle for why this reads off a smoothed fraction (_speedFractionSmoothed) instead
    // of computing the angle directly - same eased-sweep treatment applied to the dial gauges.
    public double SpeedNeedleAngle => WideGaugeAngle(_speedFractionSmoothed);

    private double TargetSpeedFraction() => Math.Clamp(SpeedValue / SpeedGaugeReferenceMax, 0.0, 1.0);

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

    // Retarder has levels (0..RetarderStepCount), not just on/off, so instead of a single lit/dim
    // telltale the icon's own letter swaps for the current level number while engaged - same idea as
    // the gear box showing an actual number rather than a generic "in gear" light. Only shown at all
    // on trucks that actually have a retarder fitted (RetarderStepCount > 0 - see HasRetarder).
    // Green, not amber - engaging it is a deliberate driving aid working as intended (same category
    // as cruise control), not a handling-change/"worth noticing" state like diff lock or lift axle.
    public bool HasRetarder => _snapshot.RetarderStepCount > 0;
    public string RetarderColor => _snapshot.RetarderLevel > 0 ? GoodColor : WarningColorDim;
    public string RetarderDisplayText => _snapshot.RetarderLevel > 0 ? _snapshot.RetarderLevel.ToString() : "R";

    // Engine brake - same green "deliberate driving aid, working as intended" treatment as retarder
    // above. Letter matches whichever term that game's own audience actually uses: ATS drivers know
    // it as the Jake brake; ETS2's European audience knows the same feature as the engine brake.
    public string JakeBrakeLetter => _snapshot.Game == SimGame.Ats ? "J" : "E";
    public string JakeBrakeColor => _snapshot.MotorBrakeOn ? GoodColor : WarningColorDim;

    // In-game clock (24h) - shown regardless of whether a job's active, unlike RemainingTimeDisplay
    // below which only means something mid-delivery.
    public string GameClockDisplay => _snapshot.SdkActive ? _snapshot.GameTime.ToString("HH:mm") : "--:--";

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
    public string DistanceTraveledDisplay => FormatDistance(TraveledKm());

    private double TraveledKm()
    {
        var baselineMeters = _initialNavigationDistanceMeters ?? _snapshot.NavigationDistanceMeters;
        var traveledKm = (baselineMeters - _snapshot.NavigationDistanceMeters) / 1000.0;
        return Math.Max(0, traveledKm);
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

    // AdBlue isn't part of the per-trip consumption estimate the way fuel is (no equivalent
    // trip-needs forecast for it).
    public bool HasAdBlue => _snapshot.AdBlueCapacityLiters > 0.1f;

    private const string WarningColorRed = "#FF4C4C";
    private const string WarningColorAmber = "#FFC24C";
    private const string WarningColorDim = "#555555";

    // Mirrors the exact dashboard warning lights ATS/ETS2 already computes - no thresholds of our
    // own to get wrong. Rendered as individual telltales (like a real instrument cluster), each
    // dimmed grey until its own condition trips. Colors follow real vehicle dashboard convention:
    // red for anything safety-critical (brakes, oil pressure, overheating, charging system failure),
    // amber for anything that just needs attention soon (fuel, AdBlue).
    //
    // All gated on ElectricEnabled - same reasoning as the fuel/AdBlue needle sitting at Empty with
    // no power (see FuelNeedleAngle): a warning LIGHT needs electricity to light up just as much as a
    // needle needs it to hold a reading. The game's own WarnLow*/Warn* flags don't reset themselves
    // just because the electrics are off (they're still tracking the underlying real condition, e.g.
    // fuel genuinely is low), so without this every telltale would stay lit through an ignition-off
    // moment even though nothing on the real dashboard could actually be glowing then.
    public string AirPressureWarnColor => _snapshot.ElectricEnabled && _snapshot.WarnLowAirPressure ? WarningColorRed : WarningColorDim;
    public string ParkingBrakeWarnColor => _snapshot.ElectricEnabled && _snapshot.WarnAirPressureEmergency ? WarningColorRed : WarningColorDim;
    public string OilPressureWarnColor => _snapshot.ElectricEnabled && _snapshot.WarnOilPressure ? WarningColorRed : WarningColorDim;
    public string WaterTempWarnColor => _snapshot.ElectricEnabled && _snapshot.WarnWaterTemperature ? WarningColorRed : WarningColorDim;
    public string FuelWarnColor => _snapshot.ElectricEnabled && _snapshot.WarnLowFuel ? WarningColorAmber : WarningColorDim;
    public string AdBlueWarnColor => _snapshot.ElectricEnabled && _snapshot.WarnLowAdBlue ? WarningColorAmber : WarningColorDim;
    // A charging-system failure is treated as critical on almost every real dashboard (you can lose
    // electrical power entirely) - red, not amber, matching that convention.
    public string BatteryWarnColor => _snapshot.ElectricEnabled && _snapshot.WarnBatteryVoltage ? WarningColorRed : WarningColorDim;

    // Fuel needle-gauge angle, in degrees for a RotateTransform pivoted at the gauge's base: -60
    // (pointing up-left) at empty, +60 (pointing up-right) at full, matching the dial arc drawn in
    // MainWindow.xaml. The needle itself stays a fixed neutral color - low fuel has its own dedicated
    // telltale (a small light near "E", like a real gauge) rather than the needle changing color.
    //
    // Real fuel gauges are electrically driven and have no power of their own to hold a position -
    // with the ignition off, both the "pull toward E" and "pull toward F" coils are unpowered, so the
    // needle just sits wherever it happens to rest, which in practice is E (it's also a deliberate
    // safety default: better a driver assumes empty than believes they have more fuel than they do).
    // Same idea applies here whenever the truck's electrics are off.
    //
    // Rather than jumping straight to the target reading, FuelNeedleAngle/AdBlueLitSegments read off
    // _fuelFractionSmoothed/_adBlueFractionSmoothed, which _gaugeAnimationTimer eases toward the real
    // target a little at a time (see OnGaugeAnimationTick) - a real needle physically sweeps to its
    // reading rather than teleporting, on startup and on every fill-up alike.
    private static double NeedleAngle(double fraction) => -60.0 + Math.Clamp(fraction, 0.0, 1.0) * 120.0;

    // Same idea as NeedleAngle, but a wider ~200° sweep for the speed/RPM dials - closer to how a real
    // speedometer/tachometer reads, rather than fuel's tighter E-to-F arc.
    private static double WideGaugeAngle(double fraction) => -100.0 + Math.Clamp(fraction, 0.0, 1.0) * 200.0;

    public double FuelNeedleAngle => NeedleAngle(_fuelFractionSmoothed);

    private double TargetFuelFraction() =>
        !_snapshot.ElectricEnabled || _snapshot.FuelCapacityLiters <= 0.1f
            ? 0.0
            : Math.Clamp((double)_snapshot.FuelLiters / _snapshot.FuelCapacityLiters, 0.0, 1.0);

    // AdBlue is a 4-segment level bar (25% per segment) rather than a needle dial, matching the
    // segmented DEF gauges real trucks use - there's no single industry-standard segment count
    // (Hino's cluster uses 9 uneven steps, for example), so 4 even quarters is a reasonable,
    // easy-to-read middle ground rather than a claimed exact match to any one manufacturer.
    // Segment N lights as soon as the level reaches into its quarter (so 1% still shows 1 bar,
    // same "never look completely empty until it basically is" convention a phone battery icon uses).
    // Same unpowered-gauge behavior as fuel: no segments lit until the electrics are on.
    private double TargetAdBlueFraction() =>
        !_snapshot.ElectricEnabled || _snapshot.AdBlueCapacityLiters <= 0.1f
            ? 0.0
            : Math.Clamp((double)_snapshot.AdBlueLiters / _snapshot.AdBlueCapacityLiters, 0.0, 1.0);

    private int AdBlueLitSegments
    {
        get
        {
            var fraction = _adBlueFractionSmoothed;
            return (int)Math.Ceiling(fraction * 4.0);
        }
    }

    public bool AdBlueSegment1Lit => AdBlueLitSegments >= 1;
    public bool AdBlueSegment2Lit => AdBlueLitSegments >= 2;
    public bool AdBlueSegment3Lit => AdBlueLitSegments >= 3;
    public bool AdBlueSegment4Lit => AdBlueLitSegments >= 4;

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

            if (_snapshot.IsAutomatic)
            {
                // No gear numbers or calibration for a torque-converter automatic - just the
                // familiar D/N/P/R. The SDK has no distinct "Park" gear (GearDashboards is only
                // forward/neutral/reverse), so Park is inferred the same way trucks without a
                // mechanical parking pawl indicate it: neutral with the parking brake set.
                if (raw < 0) return "R";
                if (raw == 0) return _snapshot.ParkingBrakeOn ? "P" : "N";
                return "D";
            }

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
        _snapshot.IsHShifter && _displayedRangeIsHigh.HasValue
            ? core + (_displayedRangeIsHigh.Value ? "H" : "L")
            : core;

    // See _displayedRangeIsHigh's declaration for why this exists instead of reading
    // _snapshot.RangeIsHigh directly.
    private void UpdateDisplayedRange(TelemetrySnapshot snapshot)
    {
        if (!snapshot.SdkActive || snapshot.GearboxFingerprint != _lastGearboxFingerprintForRange)
        {
            // Disconnected, or a different gearbox (truck swap) - nothing carried over is trustworthy.
            _displayedRangeIsHigh = null;
            _lastGearDashboardsRawForRange = null;
            _lastGearboxFingerprintForRange = snapshot.GearboxFingerprint;
        }

        if (!snapshot.IsHShifter || !snapshot.RangeIsHigh.HasValue)
        {
            return;
        }

        if (_lastGearDashboardsRawForRange != snapshot.GearDashboardsRaw)
        {
            _displayedRangeIsHigh = snapshot.RangeIsHigh;
            _lastGearDashboardsRawForRange = snapshot.GearDashboardsRaw;
        }
    }

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

    // Falls back to a plausible max before the truck's real one has come through in telemetry yet -
    // same fallback the old ring gauge used.
    private float RpmGaugeReferenceMax => _snapshot.EngineRpmMax > 0.1f ? _snapshot.EngineRpmMax : 2500f;

    // Real tachometers label ticks in "x1000 RPM" (1, 2, 3...) rather than the raw number.
    public string RpmGaugeMidLabel => $"{RpmGaugeReferenceMax / 2 / 1000.0:0.0}";
    public string RpmGaugeMaxLabel => $"{RpmGaugeReferenceMax / 1000.0:0.0}";

    public double RpmNeedleAngle => WideGaugeAngle(_rpmFractionSmoothed);

    private double TargetRpmFraction() => Math.Clamp(_snapshot.Rpm / RpmGaugeReferenceMax, 0.0, 1.0);

    public string RemainingTimeDisplay => FormatMinutes(_snapshot.RemainingDeliveryMinutes, showLatePrefix: true);

    // Only meaningful when the user has confirmed the fatigue simulation is on (see
    // AppSettings.FatigueSimulationEnabled) - the game writes the same ~12h placeholder into this
    // field whether the simulation is off or just hasn't started counting down yet, so there's no
    // way to tell those two states apart from telemetry alone.
    public string RestTimeDisplay => FormatMinutes(_snapshot.RestTimeMinutes, showLatePrefix: false);

    public string RestTimeColor =>
        _snapshot.RestTimeMinutes <= 0 ? WarningColorRed
        : _snapshot.RestTimeMinutes <= 60 ? WarningColorAmber
        : GoodColor;

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

    // Separate from SpeedSectionVisibility (the current-speed ring) - the sign shares a row with the
    // turn signal arrows but toggles independently now.
    public Visibility SpeedSignVisibility =>
        !_settings.ShowSpeedSign ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    // Same "hidden while disconnected" convention as everything else - previously missing entirely,
    // so the clock kept showing its "--:--" placeholder alone even when the rest of the HUD (speed
    // sign, turn signals) correctly hid itself while no game was running.
    public Visibility GameClockVisibility =>
        !_settings.ShowGameClock ? Visibility.Collapsed
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

    public Visibility RestTimeVisibility =>
        !_settings.FatigueSimulationEnabled ? Visibility.Collapsed
        : IsConnected ? Visibility.Visible : Visibility.Hidden;

    private int ConvertSpeed(float kph) =>
        (int)Math.Round(_settings.UseMetric ? kph : kph * KmToMiles);

    // Path data for a ring-gauge fill arc, starting at 12 o'clock and sweeping clockwise by
    // `fraction` of a full turn. A 70x70 box (radius 31, centered at 35,35) matches the ring boxes
    // in MainWindow.xaml. Clamped just under 1.0 - an ArcSegment whose start and end point coincide
    // (an exact full circle) doesn't render reliably.
    private string FormatDistance(double km)
    {
        var value = _settings.UseMetric ? km : km * KmToMiles;
        return $"{value:N0} {DistanceUnitLabel}";
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        var wasOnJob = _snapshot.OnJob;
        _snapshot = snapshot;

        UpdateDisplayedRange(snapshot);

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

            // Rebuilt every tick rather than only once at delivery - by the time JobDelivered fires,
            // the SDK's own job fields (and our OnJob-gated baseline above) may already have reset for
            // "no job", so OnJobDelivered just commits whatever was last cached here instead of trying
            // to read anything fresh at that instant.
            _pendingLogEntry = new LogEntry
            {
                DeliveredAt = DateTime.Now,
                Pickup = FormatStop(snapshot.CompanySource, snapshot.CitySource),
                DropOff = FormatStop(snapshot.CompanyDestination, snapshot.CityDestination),
                DistanceKm = TraveledKm(),
                MileageDisplay = DistanceTraveledDisplay,
                Income = snapshot.Income,
                PayoutDisplay = IncomeDisplay,
            };
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
        OnPropertyChanged(nameof(RpmGaugeMidLabel));
        OnPropertyChanged(nameof(RpmGaugeMaxLabel));
        OnPropertyChanged(nameof(SpeedColor));
        OnPropertyChanged(nameof(SpeedGaugeMidLabel));
        OnPropertyChanged(nameof(SpeedGaugeMaxLabel));
        OnPropertyChanged(nameof(SpeedSectionVisibility));
        OnPropertyChanged(nameof(SpeedSignVisibility));
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
        OnPropertyChanged(nameof(HasRetarder));
        OnPropertyChanged(nameof(RetarderColor));
        OnPropertyChanged(nameof(RetarderDisplayText));
        OnPropertyChanged(nameof(JakeBrakeLetter));
        OnPropertyChanged(nameof(JakeBrakeColor));
        OnPropertyChanged(nameof(GameClockDisplay));
        OnPropertyChanged(nameof(GameClockVisibility));
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
        OnPropertyChanged(nameof(AdBlueVisibility));
        OnPropertyChanged(nameof(AirPressureWarnColor));
        OnPropertyChanged(nameof(ParkingBrakeWarnColor));
        OnPropertyChanged(nameof(OilPressureWarnColor));
        OnPropertyChanged(nameof(WaterTempWarnColor));
        OnPropertyChanged(nameof(FuelWarnColor));
        OnPropertyChanged(nameof(AdBlueWarnColor));
        OnPropertyChanged(nameof(BatteryWarnColor));
        OnPropertyChanged(nameof(WarningsVisibility));
        OnPropertyChanged(nameof(RestTimeDisplay));
        OnPropertyChanged(nameof(RestTimeColor));
        OnPropertyChanged(nameof(RestTimeVisibility));
    }

    // Eases _fuelFractionSmoothed/_adBlueFractionSmoothed toward whatever the telemetry currently
    // says the real level is, a little at a time, so the needle/bar visibly sweep rather than jump.
    // Independent of OnSnapshotUpdated - the target can change at the telemetry poll rate, but this
    // ticks faster so the motion itself stays smooth regardless of how chunky the underlying data is.
    private void OnGaugeAnimationTick(object? sender, EventArgs e)
    {
        var fuelMoved = EaseTowards(ref _fuelFractionSmoothed, TargetFuelFraction());
        var adBlueMoved = EaseTowards(ref _adBlueFractionSmoothed, TargetAdBlueFraction());
        var speedMoved = EaseTowards(ref _speedFractionSmoothed, TargetSpeedFraction());
        var rpmMoved = EaseTowards(ref _rpmFractionSmoothed, TargetRpmFraction());

        if (fuelMoved)
        {
            OnPropertyChanged(nameof(FuelNeedleAngle));
        }

        if (adBlueMoved)
        {
            OnPropertyChanged(nameof(AdBlueSegment1Lit));
            OnPropertyChanged(nameof(AdBlueSegment2Lit));
            OnPropertyChanged(nameof(AdBlueSegment3Lit));
            OnPropertyChanged(nameof(AdBlueSegment4Lit));
        }

        if (speedMoved)
        {
            OnPropertyChanged(nameof(SpeedNeedleAngle));
        }

        if (rpmMoved)
        {
            OnPropertyChanged(nameof(RpmNeedleAngle));
        }
    }

    // Simple exponential ease - closes 12% of the remaining distance to the target per tick, which at
    // ~30 ticks/sec settles a full empty-to-full sweep in a little under a second, similar to a real
    // gauge's startup sweep. Snaps the last fraction of a percent to the exact target so it doesn't
    // creep forever without ever quite arriving.
    private static bool EaseTowards(ref double current, double target)
    {
        const double rate = 0.12;
        const double epsilon = 0.0008;

        var delta = target - current;
        if (Math.Abs(delta) < epsilon)
        {
            if (current == target) return false;
            current = target;
            return true;
        }

        current += delta * rate;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _telemetryService.SnapshotUpdated -= OnSnapshotUpdated;
        _telemetryService.JobDelivered -= OnJobDelivered;
        _telemetryService.JobCancelled -= OnJobCancelled;
        _updateCheckService.Checked -= OnUpdateChecked;
        _gaugeAnimationTimer.Stop();
        _gaugeAnimationTimer.Tick -= OnGaugeAnimationTick;
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
