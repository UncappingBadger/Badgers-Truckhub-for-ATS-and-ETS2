using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Alternative telemetry source: reads the independent SCS Telemetry Hub plugin's local HTTP/SSE
/// feed (see F:\Claude Projects\ScsTelemetryHub - a from-scratch replacement for RenCloud's
/// unmaintained-since-2023 shared-memory plugin, built after that plugin was confirmed unable to
/// see ATS's Road Trip DLC "Quick Job" data at all) instead of reading shared memory directly.
/// Not yet the default - see TelemetryService.UseScsTelemetryHub. This class only produces raw
/// TelemetrySnapshot values and named gameplay events; all edge-detection/logging (job started,
/// connected/disconnected) lives in TelemetryService so it behaves identically regardless of source.
/// </summary>
public sealed class ScsTelemetryHubClient : IDisposable
{
    private const string BaseUrl = "http://127.0.0.1:38471";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient = new();
    private readonly CancellationTokenSource _cts = new();

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    /// <summary>Raw gameplay event name, one of: job_delivered, job_cancelled, player_fined,
    /// tollgate_paid, ferry_used, train_used - matching the plugin's own gameplay::events names.</summary>
    public event Action<string>? GameplayEvent;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndStreamAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"ScsTelemetryHubClient: stream error, retrying - {ex.Message}");
            }

            SnapshotUpdated?.Invoke(TelemetrySnapshot.Disconnected);
            try
            {
                // The plugin only exists while the game is running with it loaded - most retries
                // are just "game isn't open yet", not a real fault, so this stays quiet by design.
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAndStreamAsync(CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(
            $"{BaseUrl}/stream", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null)
            {
                return; // server closed the connection (plugin shut down) - RunAsync will retry.
            }
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                HandleMessage(line[6..]);
            }
        }
    }

    private void HandleMessage(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            AppLogger.Log($"ScsTelemetryHubClient: malformed message ignored - {ex.Message}");
            return;
        }

        using (doc)
        {
            var type = doc.RootElement.GetProperty("type").GetString();
            switch (type)
            {
                case "snapshot":
                    var message = doc.RootElement.Deserialize<HubSnapshotMessage>(JsonOptions);
                    if (message is not null)
                    {
                        SnapshotUpdated?.Invoke(BuildSnapshot(message));
                    }
                    break;
                case "event":
                    var name = doc.RootElement.GetProperty("data").GetProperty("name").GetString();
                    if (name is not null)
                    {
                        GameplayEvent?.Invoke(name);
                    }
                    break;
                // "job" fires immediately on a job change for consumers that want lower latency
                // than the throttled snapshot tick - not needed here, the very next "snapshot"
                // message (at most SNAPSHOT_INTERVAL_MICROSECONDS later) already carries it.
                // "disconnected" fires on plugin shutdown - ConnectAndStreamAsync's own EOF/retry
                // path already covers that same transition without needing this message too.
            }
        }
    }

    private static TelemetrySnapshot BuildSnapshot(HubSnapshotMessage msg)
    {
        var data = msg.Data;
        var job = msg.Job;
        var truckSpec = msg.TruckSpec;
        var trailerSpec = msg.TrailerSpec;

        return new TelemetrySnapshot
        {
            SdkActive = true,
            Paused = data.GamePaused,
            Game = msg.Game switch
            {
                "ats" => SimGame.Ats,
                "ets2" => SimGame.Ets2,
                _ => SimGame.Unknown,
            },
            SpeedKph = data.SpeedMps * 3.6f,
            SpeedLimitKph = data.NavigationSpeedLimitMps * 3.6f,
            CruiseControlOn = data.CruiseControlMps > 0f,
            CruiseControlSpeedKph = data.CruiseControlMps * 3.6f,
            ParkingBrakeOn = data.ParkingBrake,
            ElectricEnabled = data.ElectricEnabled,
            TurnSignalLeftOn = data.LightLblinker,
            TurnSignalRightOn = data.LightRblinker,
            SidelightsOn = data.LightParking,
            LowBeamOn = data.LightLowBeam,
            HighBeamOn = data.LightHighBeam,
            BeaconOn = data.LightBeacon,
            DifferentialLockOn = data.DifferentialLock,
            LiftAxleUp = data.LiftAxle,
            HasLiftAxle = Array.Exists(truckSpec.WheelLiftable, l => l),
            RetarderLevel = data.RetarderLevel,
            RetarderStepCount = truckSpec.RetarderStepCount,
            MotorBrakeOn = data.MotorBrake,
            GameTime = DateTime.UnixEpoch.AddMinutes(data.GameTimeMinutes),
            OnJob = job.Active,
            Income = job.Income,
            PlannedDistanceKm = job.PlannedDistanceKm,
            NavigationDistanceMeters = data.NavigationDistanceM,
            PositionX = data.Placement.Position.X,
            PositionZ = data.Placement.Position.Z,
            HeadingUnit = data.Placement.Orientation.Heading,
            CitySource = job.SourceCity,
            CityDestination = job.DestinationCity,
            CompanySource = job.SourceCompany,
            CompanyDestination = job.DestinationCompany,
            CityDestinationId = job.DestinationCityId,
            CompanyDestinationId = job.DestinationCompanyId,
            CargoName = job.Cargo,
            CargoMassKg = job.CargoMassKg,
            FuelLiters = data.FuelLiters,
            FuelCapacityLiters = truckSpec.FuelCapacityLiters,
            FuelAverageConsumptionLPerKm = data.FuelAverageConsumption,
            AdBlueLiters = data.AdblueLiters,
            AdBlueCapacityLiters = truckSpec.AdblueCapacityLiters,
            WarnLowAirPressure = data.BrakeAirPressureWarning,
            WarnAirPressureEmergency = data.BrakeAirPressureEmergency,
            WarnLowFuel = data.FuelWarning,
            WarnLowAdBlue = data.AdblueWarning,
            WarnOilPressure = data.OilPressureWarning,
            WarnWaterTemperature = data.WaterTemperatureWarning,
            WarnBatteryVoltage = data.BatteryVoltageWarning,
            RemainingDeliveryMinutes = (int)(job.DeliveryTimeSeconds / 60),
            RestTimeMinutes = data.NextRestStopMinutes,
            Rpm = data.EngineRpm,
            EngineRpmMax = truckSpec.RpmLimit,
            IsHShifter = truckSpec.ShifterType == "hshifter",
            IsAutomatic = truckSpec.ShifterType == "automatic",
            HShifterSlot = data.HshifterSlot,
            SplitterHigh = data.HshifterSelectors.Count > 0 && data.HshifterSelectors[0],
            GearboxFingerprint = $"{truckSpec.ForwardGearCount}|{truckSpec.ReverseGearCount}|{msg.Hshifter.SelectorCount}",
            SelectorCount = msg.Hshifter.SelectorCount,
            GearDashboardsRaw = data.DisplayedGear,
            RangeIsHigh = data.HshifterSelectors.Count > 1 ? data.HshifterSelectors[1] : (bool?)null,

            OilPressurePsi = data.OilPressure,
            OilTemperatureC = data.OilTemperature,
            WaterTemperatureC = data.WaterTemperature,
            BatteryVoltage = data.BatteryVoltage,
            BrakeAirPressurePsi = data.BrakeAirPressure,
            BrakeTemperatureC = data.BrakeTemperature,
            OdometerKm = data.OdometerKm,

            EngineWear = data.WearEngine,
            TransmissionWear = data.WearTransmission,
            CabinWear = data.WearCabin,
            ChassisWear = data.WearChassis,
            WheelsWearAvg = data.WearWheels,
            TruckSuspDeflection = Map(data.TruckWheels, w => w.SuspensionDeflection),
            TruckWheelLiftable = truckSpec.WheelLiftable,
            TruckWheelLift = Map(data.TruckWheels, w => w.Lift),
            TruckWheelPowered = truckSpec.WheelPowered,
            TruckWheelSteerable = truckSpec.WheelSteerable,

            TrailerAttached = data.Trailer.Connected,
            TrailerName = trailerSpec.Name,
            TrailerBodyWear = data.Trailer.WearBody,
            TrailerCargoWear = data.Trailer.CargoDamage,
            TrailerChassisWear = data.Trailer.WearChassis,
            TrailerWheelsWear = data.Trailer.WearWheels,
            TrailerSuspDeflection = Map(data.Trailer.Wheels, w => w.SuspensionDeflection),
            TrailerWheelLift = Map(data.Trailer.Wheels, w => w.Lift),
            TrailerWheelOnGround = Map(data.Trailer.Wheels, w => w.OnGround),
            TrailerWheelLiftable = trailerSpec.WheelLiftable,
            TrailerLiftAxleUp = data.TrailerLiftAxle,
            TrailerLiftAxleIndicatorOn = data.TrailerLiftAxleIndicator,
        };
    }

    private static float[] Map(List<HubWheelState> wheels, Func<HubWheelState, float> select)
    {
        var result = new float[wheels.Count];
        for (var i = 0; i < wheels.Count; i++) result[i] = select(wheels[i]);
        return result;
    }

    private static bool[] Map(List<HubWheelState> wheels, Func<HubWheelState, bool> select)
    {
        var result = new bool[wheels.Count];
        for (var i = 0; i < wheels.Count; i++) result[i] = select(wheels[i]);
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _httpClient.Dispose();
    }

    // --- Wire model, mirroring the plugin's JSON exactly (F:\Claude Projects\ScsTelemetryHub\plugin\src\lib.rs) ---

    private sealed class HubSnapshotMessage
    {
        public string Game { get; set; } = "";
        public HubTruckSpec TruckSpec { get; set; } = new();
        public HubTrailerSpec TrailerSpec { get; set; } = new();
        public HubHshifterInfo Hshifter { get; set; } = new();
        public HubJob Job { get; set; } = new();
        public HubData Data { get; set; } = new();
    }

    private sealed class HubTruckSpec
    {
        public float FuelCapacityLiters { get; set; }
        public float AdblueCapacityLiters { get; set; }
        public float RpmLimit { get; set; }
        public uint ForwardGearCount { get; set; }
        public uint ReverseGearCount { get; set; }
        public uint RetarderStepCount { get; set; }
        public bool[] WheelSteerable { get; set; } = Array.Empty<bool>();
        public bool[] WheelPowered { get; set; } = Array.Empty<bool>();
        public bool[] WheelLiftable { get; set; } = Array.Empty<bool>();
        public string ShifterType { get; set; } = "";
    }

    private sealed class HubTrailerSpec
    {
        public string Name { get; set; } = "";
        public bool[] WheelLiftable { get; set; } = Array.Empty<bool>();
    }

    private sealed class HubHshifterSlot
    {
        public uint Slot { get; set; }
        public int Gear { get; set; }
        public uint Selectors { get; set; }
    }

    private sealed class HubHshifterInfo
    {
        public uint SelectorCount { get; set; }
        public List<HubHshifterSlot> Slots { get; set; } = new();
    }

    private sealed class HubJob
    {
        public bool Active { get; set; }
        public string Cargo { get; set; } = "";
        public float CargoMassKg { get; set; }
        public string SourceCity { get; set; } = "";
        public string SourceCompany { get; set; } = "";
        public string DestinationCity { get; set; } = "";
        public string DestinationCityId { get; set; } = "";
        public string DestinationCompany { get; set; } = "";
        public string DestinationCompanyId { get; set; } = "";
        public ulong Income { get; set; }
        public uint DeliveryTimeSeconds { get; set; }
        public uint PlannedDistanceKm { get; set; }
    }

    private sealed class HubVec3
    {
        public double X { get; set; }
        public double Z { get; set; }
    }

    private sealed class HubOrientation
    {
        public float Heading { get; set; }
    }

    private sealed class HubPlacement
    {
        public HubVec3 Position { get; set; } = new();
        public HubOrientation Orientation { get; set; } = new();
    }

    private sealed class HubWheelState
    {
        public float SuspensionDeflection { get; set; }
        public bool OnGround { get; set; }
        public float Lift { get; set; }
    }

    private sealed class HubTrailerState
    {
        public bool Connected { get; set; }
        public float CargoDamage { get; set; }
        public float WearBody { get; set; }
        public float WearChassis { get; set; }
        public float WearWheels { get; set; }
        public List<HubWheelState> Wheels { get; set; } = new();
    }

    private sealed class HubData
    {
        public bool GamePaused { get; set; }
        public HubPlacement Placement { get; set; } = new();
        public float SpeedMps { get; set; }
        public float EngineRpm { get; set; }
        public int DisplayedGear { get; set; }
        public float CruiseControlMps { get; set; }
        public bool ParkingBrake { get; set; }
        public bool MotorBrake { get; set; }
        public uint RetarderLevel { get; set; }
        public float BrakeAirPressure { get; set; }
        public bool BrakeAirPressureWarning { get; set; }
        public bool BrakeAirPressureEmergency { get; set; }
        public float BrakeTemperature { get; set; }
        public float FuelLiters { get; set; }
        public bool FuelWarning { get; set; }
        public float FuelAverageConsumption { get; set; }
        public float AdblueLiters { get; set; }
        public bool AdblueWarning { get; set; }
        public float OilPressure { get; set; }
        public bool OilPressureWarning { get; set; }
        public float OilTemperature { get; set; }
        public float WaterTemperature { get; set; }
        public bool WaterTemperatureWarning { get; set; }
        public float BatteryVoltage { get; set; }
        public bool BatteryVoltageWarning { get; set; }
        public bool ElectricEnabled { get; set; }
        public bool LightLblinker { get; set; }
        public bool LightRblinker { get; set; }
        public bool LightParking { get; set; }
        public bool LightLowBeam { get; set; }
        public bool LightHighBeam { get; set; }
        public bool LightBeacon { get; set; }
        public bool DifferentialLock { get; set; }
        public bool LiftAxle { get; set; }
        public bool TrailerLiftAxle { get; set; }
        public bool TrailerLiftAxleIndicator { get; set; }
        public float WearEngine { get; set; }
        public float WearTransmission { get; set; }
        public float WearCabin { get; set; }
        public float WearChassis { get; set; }
        public float WearWheels { get; set; }
        public float OdometerKm { get; set; }
        public float NavigationDistanceM { get; set; }
        public float NavigationSpeedLimitMps { get; set; }
        public List<HubWheelState> TruckWheels { get; set; } = new();
        public uint HshifterSlot { get; set; }
        public List<bool> HshifterSelectors { get; set; } = new();
        public HubTrailerState Trailer { get; set; } = new();
        public uint GameTimeMinutes { get; set; }
        public int NextRestStopMinutes { get; set; }
    }
}
