using System;
using System.Threading;
using SCSSdkClient;
using SCSSdkClient.Object;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Thin wrapper around SCSSdkClient that polls the game's shared-memory telemetry map
/// and republishes it as our own plain DTO, marshaled onto the thread that constructed this service.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    /// <summary>
    /// Switches the telemetry source from RenCloud's shared-memory plugin (legacy) to the independent
    /// SCS Telemetry Hub plugin's local HTTP/SSE feed (see F:\Claude Projects\ScsTelemetryHub) - built
    /// to see ATS's Road Trip DLC "Quick Job" data, which RenCloud's plugin (unmaintained since 2023)
    /// never will. Flipped to true after a full live ATS+ETS2 test session confirmed parity (both
    /// truck and car mode, both games) - flip back to false to fall back to RenCloud instantly if
    /// something regresses, same dormant-flag pattern as GameMapProfile.Ets2Enabled.
    /// </summary>
    private const bool UseScsTelemetryHub = true;

    private readonly SCSSdkTelemetry? _telemetry;
    private readonly ScsTelemetryHubClient? _hubClient;
    private readonly SynchronizationContext _uiContext;

    private TelemetrySnapshot _lastSnapshot = TelemetrySnapshot.Disconnected;
    private bool _lastSdkActive;
    private bool _lastOnJob;

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    /// <summary>
    /// Fires when a job is successfully delivered (not cancelled). Carries no payload - subscribers
    /// that need the job's details should track them from SnapshotUpdated while OnJob is true, since
    /// by the time this fires the telemetry's own job fields may already be cleared for "no job".
    /// </summary>
    public event Action? JobDelivered;

    /// <summary>Fires when a job is cancelled/failed - the counterpart to JobDelivered, so subscribers
    /// know to discard whatever they were tracking instead of logging it as a completed job.</summary>
    public event Action? JobCancelled;

    /// <summary>Latest snapshot, readable synchronously (e.g. from a global hotkey handler) without waiting for the next poll event.</summary>
    public TelemetrySnapshot LastSnapshot => _lastSnapshot;

    public TelemetryService(int pollIntervalMs = 250)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TelemetryService must be constructed on a thread with a SynchronizationContext (e.g. the UI thread).");

        if (UseScsTelemetryHub)
        {
            _hubClient = new ScsTelemetryHubClient();
            _hubClient.SnapshotUpdated += snapshot => UpdateCommon(snapshot);
            _hubClient.GameplayEvent += OnHubGameplayEvent;
            _hubClient.Start();
            return;
        }

        _telemetry = new SCSSdkTelemetry(pollIntervalMs);
        _telemetry.Data += OnData;

        _telemetry.JobDelivered += (_, _) =>
        {
            AppLogger.Log("Job delivered");
            _uiContext.Post(_ => JobDelivered?.Invoke(), null);
        };
        _telemetry.JobCancelled += (_, _) =>
        {
            AppLogger.Log("Job cancelled");
            _uiContext.Post(_ => JobCancelled?.Invoke(), null);
        };
        _telemetry.Fined += (_, _) => AppLogger.Log("Fined by police");
        _telemetry.Tollgate += (_, _) => AppLogger.Log("Tollgate paid");
        _telemetry.Ferry += (_, _) => AppLogger.Log("Ferry used");
        _telemetry.Train += (_, _) => AppLogger.Log("Train used");
        _telemetry.RefuelStart += (_, _) => AppLogger.Log("Refueling started");
        _telemetry.RefuelEnd += (_, _) => AppLogger.Log("Refueling ended");
        _telemetry.RefuelPayed += (_, _) => AppLogger.Log("Refuel paid");
    }

    /// <summary>Named gameplay events from ScsTelemetryHubClient - the new-source equivalent of the
    /// RenCloud-specific Fined/Tollgate/Ferry/Train/JobDelivered/JobCancelled events wired above.
    /// refuel_started/ended/paid have no official SDK signal either (confirmed: SDK 1.14's gameplay-
    /// event catalog has none) - the plugin ports RenCloud's own tick-based fuel-delta heuristic
    /// (scs-telemetry.cpp's telemetry_frame_start, found via a background research agent) directly,
    /// so these fire on the same conditions RenCloud's RefuelStart/End/Payed always have.</summary>
    private void OnHubGameplayEvent(string name)
    {
        switch (name)
        {
            case "job_delivered":
                AppLogger.Log("Job delivered");
                _uiContext.Post(_ => JobDelivered?.Invoke(), null);
                break;
            case "job_cancelled":
                AppLogger.Log("Job cancelled");
                _uiContext.Post(_ => JobCancelled?.Invoke(), null);
                break;
            case "player_fined": AppLogger.Log("Fined by police"); break;
            case "tollgate_paid": AppLogger.Log("Tollgate paid"); break;
            case "ferry_used": AppLogger.Log("Ferry used"); break;
            case "train_used": AppLogger.Log("Train used"); break;
            case "refuel_started": AppLogger.Log("Refueling started"); break;
            case "refuel_ended": AppLogger.Log("Refueling ended"); break;
            case "refuel_paid": AppLogger.Log("Refuel paid"); break;
        }
    }

    private void OnData(SCSTelemetry data, bool newTimestamp) => UpdateCommon(Convert(data));

    /// <summary>Single place for everything that must behave identically regardless of which
    /// telemetry source is active: connected/disconnected logging, job-started detection (an edge
    /// on OnJob rather than a RenCloud-specific event, so it works the same for both sources), and
    /// publishing the snapshot itself.</summary>
    private void UpdateCommon(TelemetrySnapshot snapshot)
    {
        if (snapshot.SdkActive != _lastSdkActive)
        {
            AppLogger.Log(snapshot.SdkActive ? $"Connected to {snapshot.Game}" : "Disconnected (game closed or SDK inactive)");
            _lastSdkActive = snapshot.SdkActive;
        }

        if (snapshot.OnJob && !_lastOnJob)
        {
            AppLogger.Log(
                $"Job started: {snapshot.CompanySource}/{snapshot.CitySource} -> " +
                $"{snapshot.CompanyDestination}/{snapshot.CityDestination}, " +
                $"cargo={snapshot.CargoName} {snapshot.CargoMassKg / 1000:0}T, " +
                $"income={snapshot.Income}, plannedKm={snapshot.PlannedDistanceKm}");
        }
        _lastOnJob = snapshot.OnJob;

        _lastSnapshot = snapshot;
        _uiContext.Post(_ => SnapshotUpdated?.Invoke(snapshot), null);
    }

    private static TelemetrySnapshot Convert(SCSTelemetry data)
    {
        if (!data.SdkActive)
        {
            return TelemetrySnapshot.Disconnected;
        }

        var isHShifter = data.TruckValues.ConstantsValues.MotorValues.ShifterTypeValue == ShifterType.HShifter;
        var isAutomatic = data.TruckValues.ConstantsValues.MotorValues.ShifterTypeValue == ShifterType.Automatic;
        var hShifterSlot = data.TruckValues.CurrentValues.MotorValues.GearValues.HShifterSlot;
        var gearDashboards = data.TruckValues.CurrentValues.DashboardValues.GearDashboards;
        var selector = data.TruckValues.CurrentValues.MotorValues.GearValues.HShifterSelector;
        var splitterHigh = isHShifter && selector is { Length: > 0 } && selector[0];

        var motor = data.TruckValues.ConstantsValues.MotorValues;
        var ratioSignature = motor.GearRatiosForward == null
            ? ""
            : string.Join(",", Array.ConvertAll(motor.GearRatiosForward, r => r.ToString("0.000")));
        var gearboxFingerprint = $"{motor.ForwardGearCount}|{motor.ReverseGearCount}|{motor.SelectorCount}|{ratioSignature}";

        // First attached trailer only - see TelemetrySnapshot's own comment on why (multi-trailer
        // combos exist in-game but a single trailer covers the instrument-cluster use case this is
        // for). TrailerValues can be null/empty with nothing hitched.
        var trailer = data.TrailerValues is { Length: > 0 } ? data.TrailerValues[0] : null;
        var trailerAttached = trailer?.Attached ?? false;
        var trailerWheelCount = trailerAttached ? trailer!.WheelsConstant.Count : 0;

        return new TelemetrySnapshot
        {
            SdkActive = true,
            Paused = data.Paused,
            Game = data.Game switch
            {
                SCSGame.Ets2 => SimGame.Ets2,
                SCSGame.Ats => SimGame.Ats,
                _ => SimGame.Unknown,
            },
            SpeedKph = data.TruckValues.CurrentValues.DashboardValues.Speed.Kph,
            SpeedLimitKph = data.NavigationValues.SpeedLimit.Kph,
            CruiseControlOn = data.TruckValues.CurrentValues.DashboardValues.CruiseControl,
            CruiseControlSpeedKph = data.TruckValues.CurrentValues.DashboardValues.CruiseControlSpeed.Kph,
            ParkingBrakeOn = data.TruckValues.CurrentValues.MotorValues.BrakeValues.ParkingBrake,
            ElectricEnabled = data.TruckValues.CurrentValues.ElectricEnabled,
            TurnSignalLeftOn = data.TruckValues.CurrentValues.LightsValues.BlinkerLeftOn,
            TurnSignalRightOn = data.TruckValues.CurrentValues.LightsValues.BlinkerRightOn,
            SidelightsOn = data.TruckValues.CurrentValues.LightsValues.Parking,
            LowBeamOn = data.TruckValues.CurrentValues.LightsValues.BeamLow,
            HighBeamOn = data.TruckValues.CurrentValues.LightsValues.BeamHigh,
            BeaconOn = data.TruckValues.CurrentValues.LightsValues.Beacon,
            DifferentialLockOn = data.TruckValues.CurrentValues.DifferentialLock,
            LiftAxleUp = data.TruckValues.CurrentValues.LiftAxle,
            HasLiftAxle = Array.Exists(data.TruckValues.ConstantsValues.WheelsValues.Liftable ?? Array.Empty<bool>(), l => l),
            RetarderLevel = data.TruckValues.CurrentValues.MotorValues.BrakeValues.RetarderLevel,
            RetarderStepCount = data.TruckValues.ConstantsValues.MotorValues.RetarderStepCount,
            MotorBrakeOn = data.TruckValues.CurrentValues.MotorValues.BrakeValues.MotorBrake,
            GameTime = data.CommonValues.GameTime.Date,
            OnJob = data.SpecialEventsValues.OnJob,
            Income = data.JobValues.Income,
            PlannedDistanceKm = data.JobValues.PlannedDistanceKm,
            NavigationDistanceMeters = data.NavigationValues.NavigationDistance,
            PositionX = data.TruckValues.CurrentValues.PositionValue.Position.X,
            PositionZ = data.TruckValues.CurrentValues.PositionValue.Position.Z,
            HeadingUnit = data.TruckValues.CurrentValues.PositionValue.Orientation.Heading,
            CitySource = data.JobValues.CitySource ?? "",
            CityDestination = data.JobValues.CityDestination ?? "",
            CompanySource = data.JobValues.CompanySource ?? "",
            CompanyDestination = data.JobValues.CompanyDestination ?? "",
            CityDestinationId = data.JobValues.CityDestinationId ?? "",
            CompanyDestinationId = data.JobValues.CompanyDestinationId ?? "",
            CargoName = data.JobValues.CargoValues.Name ?? "",
            CargoMassKg = data.JobValues.CargoValues.Mass,
            FuelLiters = data.TruckValues.CurrentValues.DashboardValues.FuelValue.Amount,
            FuelCapacityLiters = data.TruckValues.ConstantsValues.CapacityValues.Fuel,
            FuelAverageConsumptionLPerKm = data.TruckValues.CurrentValues.DashboardValues.FuelValue.AverageConsumption,
            AdBlueLiters = data.TruckValues.CurrentValues.DashboardValues.AdBlue,
            AdBlueCapacityLiters = data.TruckValues.ConstantsValues.CapacityValues.AdBlue,
            WarnLowAirPressure = data.TruckValues.CurrentValues.DashboardValues.WarningValues.AirPressure,
            WarnAirPressureEmergency = data.TruckValues.CurrentValues.DashboardValues.WarningValues.AirPressureEmergency,
            WarnLowFuel = data.TruckValues.CurrentValues.DashboardValues.WarningValues.FuelW,
            WarnLowAdBlue = data.TruckValues.CurrentValues.DashboardValues.WarningValues.AdBlue,
            WarnOilPressure = data.TruckValues.CurrentValues.DashboardValues.WarningValues.OilPressure,
            WarnWaterTemperature = data.TruckValues.CurrentValues.DashboardValues.WarningValues.WaterTemperature,
            WarnBatteryVoltage = data.TruckValues.CurrentValues.DashboardValues.WarningValues.BatteryVoltage,
            RemainingDeliveryMinutes = data.JobValues.RemainingDeliveryTime.Value,
            RestTimeMinutes = data.CommonValues.NextRestStop.Value,
            Rpm = data.TruckValues.CurrentValues.DashboardValues.RPM,
            EngineRpmMax = data.TruckValues.ConstantsValues.MotorValues.EngineRpmMax,
            IsHShifter = isHShifter,
            IsAutomatic = isAutomatic,
            HShifterSlot = hShifterSlot,
            SplitterHigh = splitterHigh,
            GearboxFingerprint = gearboxFingerprint,
            SelectorCount = motor.SelectorCount,
            // Kept raw (not offset-adjusted) - GearDashboards numbers gears by overall ratio, which
            // doesn't match physical shifter position, so it's only used here for its sign
            // (neutral/reverse detection). The ViewModel applies any slot-to-gear-number offset,
            // gated on whether the current gearbox matches the one that offset was calibrated for
            // (see GearboxFingerprint) - a different gearbox can reuse slots completely differently.
            GearDashboardsRaw = gearDashboards,
            // Confirmed via live calibration: index 1 is the range toggle (false=Low, true=High).
            // Index 0 is the splitter (see SplitterHigh above), not range.
            RangeIsHigh = isHShifter && selector is { Length: > 1 }
                ? selector[1]
                : (bool?)null,

            OilPressurePsi = data.TruckValues.CurrentValues.DashboardValues.OilPressure,
            OilTemperatureC = data.TruckValues.CurrentValues.DashboardValues.OilTemperature,
            WaterTemperatureC = data.TruckValues.CurrentValues.DashboardValues.WaterTemperature,
            BatteryVoltage = data.TruckValues.CurrentValues.DashboardValues.BatteryVoltage,
            BatteryVoltageWarnThreshold = data.TruckValues.ConstantsValues.WarningFactorValues.BatteryVoltage,
            BrakeAirPressurePsi = data.TruckValues.CurrentValues.MotorValues.BrakeValues.AirPressure,
            BrakeTemperatureC = data.TruckValues.CurrentValues.MotorValues.BrakeValues.Temperature,
            OdometerKm = data.TruckValues.CurrentValues.DashboardValues.Odometer,

            EngineWear = data.TruckValues.CurrentValues.DamageValues.Engine,
            TransmissionWear = data.TruckValues.CurrentValues.DamageValues.Transmission,
            CabinWear = data.TruckValues.CurrentValues.DamageValues.Cabin,
            ChassisWear = data.TruckValues.CurrentValues.DamageValues.Chassis,
            WheelsWearAvg = data.TruckValues.CurrentValues.DamageValues.WheelsAvg,
            // Trimmed to the truck's own reported wheel count - the SDK arrays are fixed-size (up to
            // 14 slots), so a truck with fewer real axles than that would otherwise show padding
            // slots as bogus "0mm"/airborne entries. See the matching trailer comment below.
            TruckSuspDeflection = TrimToCount(data.TruckValues.CurrentValues.WheelsValues.SuspDeflection, data.TruckValues.ConstantsValues.WheelsValues.Count),
            TruckWheelLiftable = TrimToCount(data.TruckValues.ConstantsValues.WheelsValues.Liftable, data.TruckValues.ConstantsValues.WheelsValues.Count),
            TruckWheelLift = TrimToCount(data.TruckValues.CurrentValues.WheelsValues.Lift, data.TruckValues.ConstantsValues.WheelsValues.Count),
            TruckWheelPowered = TrimToCount(data.TruckValues.ConstantsValues.WheelsValues.Powered, data.TruckValues.ConstantsValues.WheelsValues.Count),
            TruckWheelSteerable = TrimToCount(data.TruckValues.ConstantsValues.WheelsValues.Steerable, data.TruckValues.ConstantsValues.WheelsValues.Count),

            TrailerAttached = trailerAttached,
            TrailerName = (trailerAttached ? trailer!.Name : null) ?? "",
            TrailerBodyWear = trailerAttached ? trailer!.DamageValues.Body : 0f,
            TrailerCargoWear = trailerAttached ? trailer!.DamageValues.Cargo : 0f,
            TrailerChassisWear = trailerAttached ? trailer!.DamageValues.Chassis : 0f,
            TrailerWheelsWear = trailerAttached ? trailer!.DamageValues.Wheels : 0f,
            // Trimmed to the trailer's own reported wheel count (WheelsConstant.Count) - trailers with
            // fewer axles than the SDK's max array size otherwise show unused slots as fake "AIRBORNE"
            // axles (lift=0, onGround=false is indistinguishable from "not there" vs "genuinely off the
            // ground"). Real liftable tag/mid-lift axles are within the reported count, so they still
            // show up (correctly, as LIFTED when raised) - this only drops slots beyond the real count.
            TrailerSuspDeflection = trailerAttached ? TrimToCount(trailer!.Wheelvalues.SuspDeflection, trailerWheelCount) : Array.Empty<float>(),
            TrailerWheelLift = trailerAttached ? TrimToCount(trailer!.Wheelvalues.Lift, trailerWheelCount) : Array.Empty<float>(),
            TrailerWheelOnGround = trailerAttached ? TrimToCount(trailer!.Wheelvalues.OnGround, trailerWheelCount) : Array.Empty<bool>(),
            TrailerWheelLiftable = trailerAttached ? TrimToCount(trailer!.WheelsConstant.Liftable, trailerWheelCount) : Array.Empty<bool>(),
            TrailerLiftAxleUp = data.TruckValues.CurrentValues.TrailerLiftAxle,
            TrailerLiftAxleIndicatorOn = data.TruckValues.CurrentValues.TrailerLiftAxleIndicator,
        };
    }

    /// <summary>Trims a fixed-size per-wheel SDK array down to a vehicle's actual reported wheel
    /// count, so slots beyond the real axle count (padding, always zero/false) never reach the UI as
    /// fake readings. Null input (no data for this vehicle) becomes an empty array.</summary>
    private static float[] TrimToCount(float[]? values, uint count) =>
        values is null ? Array.Empty<float>() : values.Length > count ? values[..(int)count] : values;

    private static bool[] TrimToCount(bool[]? values, uint count) =>
        values is null ? Array.Empty<bool>() : values.Length > count ? values[..(int)count] : values;

    public void Dispose()
    {
        if (_telemetry is not null)
        {
            _telemetry.Data -= OnData;
            _telemetry.Dispose();
        }
        _hubClient?.Dispose();
    }
}
