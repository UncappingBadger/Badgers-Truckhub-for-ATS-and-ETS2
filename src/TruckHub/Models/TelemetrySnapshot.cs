namespace TruckHub.Models;

public enum SimGame
{
    Unknown,
    Ets2,
    Ats
}

public sealed class TelemetrySnapshot
{
    public bool SdkActive { get; init; }
    public bool Paused { get; init; }
    public SimGame Game { get; init; }

    public float SpeedKph { get; init; }
    public float SpeedLimitKph { get; init; }

    public bool CruiseControlOn { get; init; }
    public float CruiseControlSpeedKph { get; init; }
    public bool ParkingBrakeOn { get; init; }

    // BlinkerLeftOn/BlinkerRightOn already reflect the actual current bulb state (not just "enabled"
    // - the game blinks these itself, and hazards drive both together), so binding straight to them
    // gives correct flashing for free without any timer on our side.
    public bool TurnSignalLeftOn { get; init; }
    public bool TurnSignalRightOn { get; init; }
    public bool SidelightsOn { get; init; }
    public bool LowBeamOn { get; init; }
    public bool HighBeamOn { get; init; }
    public bool BeaconOn { get; init; }
    public bool DifferentialLockOn { get; init; }

    // LiftAxle is the truck's own commanded state (true = lifted); HasLiftAxle comes from the
    // per-wheel Liftable capability array, so trucks that don't have one just don't show the icon
    // instead of permanently showing a meaningless "down".
    public bool LiftAxleUp { get; init; }
    public bool HasLiftAxle { get; init; }

    public bool OnJob { get; init; }
    public ulong Income { get; init; }
    public uint PlannedDistanceKm { get; init; }
    public float NavigationDistanceMeters { get; init; }

    public string CitySource { get; init; } = "";
    public string CityDestination { get; init; } = "";
    public string CompanySource { get; init; } = "";
    public string CompanyDestination { get; init; } = "";

    public string CargoName { get; init; } = "";
    public float CargoMassKg { get; init; }

    public float FuelLiters { get; init; }
    public float FuelCapacityLiters { get; init; }
    public float FuelAverageConsumptionLPerKm { get; init; }

    public float AdBlueLiters { get; init; }
    public float AdBlueCapacityLiters { get; init; }

    // Mirrors the SDK's own dashboard warning-light booleans - no thresholds computed here.
    public bool WarnLowAirPressure { get; init; }
    public bool WarnAirPressureEmergency { get; init; }
    public bool WarnLowFuel { get; init; }
    public bool WarnLowAdBlue { get; init; }
    public bool WarnOilPressure { get; init; }
    public bool WarnWaterTemperature { get; init; }
    public bool WarnBatteryVoltage { get; init; }

    public int RemainingDeliveryMinutes { get; init; }

    public int GearDashboardsRaw { get; init; }
    public float Rpm { get; init; }
    public float EngineRpmMax { get; init; }
    public bool IsHShifter { get; init; }
    public bool? RangeIsHigh { get; init; }
    public bool SplitterHigh { get; init; }
    public uint HShifterSlot { get; init; }

    /// <summary>
    /// Identifies the currently equipped gearbox (gear ratios + counts), so the app can tell whether
    /// its slot-to-gear-number calibration (which is specific to one physical gearbox layout) still
    /// applies, or whether it's looking at a different gearbox it hasn't been calibrated for.
    /// </summary>
    public string GearboxFingerprint { get; init; } = "";

    /// <summary>Number of range/splitter selector toggles this gearbox has (0, 1, or 2) - used by the
    /// calibration UI to know whether to offer splitter-range calibration at all.</summary>
    public uint SelectorCount { get; init; }

    public static TelemetrySnapshot Disconnected { get; } = new();
}
