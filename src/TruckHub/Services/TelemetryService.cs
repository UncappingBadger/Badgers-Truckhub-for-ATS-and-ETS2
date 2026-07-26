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
    private readonly SCSSdkTelemetry _telemetry;
    private readonly SynchronizationContext _uiContext;

    private TelemetrySnapshot _lastSnapshot = TelemetrySnapshot.Disconnected;
    private bool _lastSdkActive;

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    /// <summary>Latest snapshot, readable synchronously (e.g. from a global hotkey handler) without waiting for the next poll event.</summary>
    public TelemetrySnapshot LastSnapshot => _lastSnapshot;

    public TelemetryService(int pollIntervalMs = 250)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TelemetryService must be constructed on a thread with a SynchronizationContext (e.g. the UI thread).");

        _telemetry = new SCSSdkTelemetry(pollIntervalMs);
        _telemetry.Data += OnData;

        _telemetry.JobStarted += (_, _) => AppLogger.Log(
            $"Job started: {_lastSnapshot.CompanySource}/{_lastSnapshot.CitySource} -> " +
            $"{_lastSnapshot.CompanyDestination}/{_lastSnapshot.CityDestination}, " +
            $"cargo={_lastSnapshot.CargoName} {_lastSnapshot.CargoMassKg / 1000:0}T, " +
            $"income={_lastSnapshot.Income}, plannedKm={_lastSnapshot.PlannedDistanceKm}");
        _telemetry.JobDelivered += (_, _) => AppLogger.Log("Job delivered");
        _telemetry.JobCancelled += (_, _) => AppLogger.Log("Job cancelled");
        _telemetry.Fined += (_, _) => AppLogger.Log("Fined by police");
        _telemetry.Tollgate += (_, _) => AppLogger.Log("Tollgate paid");
        _telemetry.Ferry += (_, _) => AppLogger.Log("Ferry used");
        _telemetry.Train += (_, _) => AppLogger.Log("Train used");
        _telemetry.RefuelStart += (_, _) => AppLogger.Log("Refueling started");
        _telemetry.RefuelEnd += (_, _) => AppLogger.Log("Refueling ended");
        _telemetry.RefuelPayed += (_, _) => AppLogger.Log("Refuel paid");
    }

    private void OnData(SCSTelemetry data, bool newTimestamp)
    {
        var snapshot = Convert(data);

        if (snapshot.SdkActive != _lastSdkActive)
        {
            AppLogger.Log(snapshot.SdkActive ? $"Connected to {snapshot.Game}" : "Disconnected (game closed or SDK inactive)");
            _lastSdkActive = snapshot.SdkActive;
        }

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
        var hShifterSlot = data.TruckValues.CurrentValues.MotorValues.GearValues.HShifterSlot;
        var gearDashboards = data.TruckValues.CurrentValues.DashboardValues.GearDashboards;
        var selector = data.TruckValues.CurrentValues.MotorValues.GearValues.HShifterSelector;
        var splitterHigh = isHShifter && selector is { Length: > 0 } && selector[0];

        var motor = data.TruckValues.ConstantsValues.MotorValues;
        var ratioSignature = motor.GearRatiosForward == null
            ? ""
            : string.Join(",", Array.ConvertAll(motor.GearRatiosForward, r => r.ToString("0.000")));
        var gearboxFingerprint = $"{motor.ForwardGearCount}|{motor.ReverseGearCount}|{motor.SelectorCount}|{ratioSignature}";

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
            TurnSignalLeftOn = data.TruckValues.CurrentValues.LightsValues.BlinkerLeftOn,
            TurnSignalRightOn = data.TruckValues.CurrentValues.LightsValues.BlinkerRightOn,
            SidelightsOn = data.TruckValues.CurrentValues.LightsValues.Parking,
            LowBeamOn = data.TruckValues.CurrentValues.LightsValues.BeamLow,
            HighBeamOn = data.TruckValues.CurrentValues.LightsValues.BeamHigh,
            BeaconOn = data.TruckValues.CurrentValues.LightsValues.Beacon,
            DifferentialLockOn = data.TruckValues.CurrentValues.DifferentialLock,
            LiftAxleUp = data.TruckValues.CurrentValues.LiftAxle,
            HasLiftAxle = Array.Exists(data.TruckValues.ConstantsValues.WheelsValues.Liftable ?? Array.Empty<bool>(), l => l),
            OnJob = data.SpecialEventsValues.OnJob,
            Income = data.JobValues.Income,
            PlannedDistanceKm = data.JobValues.PlannedDistanceKm,
            NavigationDistanceMeters = data.NavigationValues.NavigationDistance,
            CitySource = data.JobValues.CitySource ?? "",
            CityDestination = data.JobValues.CityDestination ?? "",
            CompanySource = data.JobValues.CompanySource ?? "",
            CompanyDestination = data.JobValues.CompanyDestination ?? "",
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
            Rpm = data.TruckValues.CurrentValues.DashboardValues.RPM,
            EngineRpmMax = data.TruckValues.ConstantsValues.MotorValues.EngineRpmMax,
            IsHShifter = isHShifter,
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
        };
    }

    public void Dispose()
    {
        _telemetry.Data -= OnData;
        _telemetry.Dispose();
    }
}
