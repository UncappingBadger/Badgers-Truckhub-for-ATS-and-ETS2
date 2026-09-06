using System;

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

    /// <summary>
    /// Truck's electrics (ignition) on/off. Real gauges are electrically driven and have no power of
    /// their own to hold a reading, so they rest at their spring-loaded default - Empty, for a fuel
    /// gauge - whenever this is off. See AppSettings/MainViewModel FuelNeedleAngle.
    /// </summary>
    public bool ElectricEnabled { get; init; }

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

    // RetarderStepCount is the truck's own configured max (0 on trucks with no retarder fitted at
    // all, so the icon just doesn't show rather than always reading "R"); RetarderLevel is 0..that
    // max, 0 meaning disengaged.
    public uint RetarderLevel { get; init; }
    public uint RetarderStepCount { get; init; }

    /// <summary>Engine brake (Jake brake) - on/off, no levels.</summary>
    public bool MotorBrakeOn { get; init; }

    /// <summary>In-game time of day (the SDK's GameTime.Date - year/day portion isn't meaningful, only
    /// the time-of-day component is).</summary>
    public DateTime GameTime { get; init; }

    public bool OnJob { get; init; }
    public ulong Income { get; init; }
    public uint PlannedDistanceKm { get; init; }
    public float NavigationDistanceMeters { get; init; }

    /// <summary>Truck's world-space position (X/Z form the horizontal ground plane; Y, altitude,
    /// isn't tracked - not needed for a 2D map). Used by the GPS map's coordinate transform.</summary>
    public double PositionX { get; init; }
    public double PositionZ { get; init; }

    /// <summary>Raw SDK heading, unit range [0,1)=[0,360), 0=north, counterclockwise.</summary>
    public float HeadingUnit { get; init; }

    public string CitySource { get; init; } = "";
    public string CityDestination { get; init; } = "";
    public string CompanySource { get; init; } = "";
    public string CompanyDestination { get; init; } = "";

    /// <summary>Raw internal tokens (not the localized display names above) - e.g. "topeka",
    /// "flv_food_str". Used by the GPS map's routing to look up the exact destination node,
    /// matching the same token format the route graph's company table already uses.</summary>
    public string CityDestinationId { get; init; } = "";
    public string CompanyDestinationId { get; init; } = "";

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

    /// <summary>
    /// Minutes until the driver needs a rest stop, per the game's optional fatigue simulation.
    /// Only meaningful when that simulation is enabled in-game - see AppSettings.FatigueSimulationEnabled.
    /// </summary>
    public int RestTimeMinutes { get; init; }

    public int GearDashboardsRaw { get; init; }
    public float Rpm { get; init; }
    public float EngineRpmMax { get; init; }
    public bool IsHShifter { get; init; }

    /// <summary>
    /// True when the player's shifter type is the SDK's "Automatic" (torque-converter style, no
    /// manual gear selection) - distinct from IsHShifter=false, which also covers plain sequential/
    /// "Manual" mode. See MainViewModel.GearDisplay for the D/N/P/R switch this drives.
    /// </summary>
    public bool IsAutomatic { get; init; }
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

    // --- EXT tab: extended instrumentation the main dash doesn't surface ---
    // Raw values behind warning lights the main dash already shows as on/off booleans (WarnOilPressure
    // etc. above) - those stay as-is, these are the actual gauge readings for a real analog display.
    public float OilPressurePsi { get; init; }
    public float OilTemperatureC { get; init; }
    public float WaterTemperatureC { get; init; }
    public float BatteryVoltage { get; init; }

    /// <summary>Per-vehicle voltage below which the game's own low-battery warning activates - ATS
    /// trucks run 12V electrical systems, ETS2 trucks run 24V, so this (not a fixed number) is what
    /// the EXT battery gauge scales its dial range around, keeping the gauge correct across both.</summary>
    public float BatteryVoltageWarnThreshold { get; init; }
    public float BrakeAirPressurePsi { get; init; }
    public float BrakeTemperatureC { get; init; }

    /// <summary>Truck total lifetime odometer in km - distinct from PlannedDistanceKm/
    /// NavigationDistanceMeters, which are per-job, not lifetime.</summary>
    public float OdometerKm { get; init; }

    // Wear <0;1> per accessory - 0 = undamaged.
    public float EngineWear { get; init; }
    public float TransmissionWear { get; init; }
    public float CabinWear { get; init; }
    public float ChassisWear { get; init; }
    public float WheelsWearAvg { get; init; }

    /// <summary>Vertical wheel-axis displacement in meters, one entry per truck wheel - the closest
    /// proxy the SDK exposes for "how loaded is this axle" (more sag = more weight), not a calibrated
    /// weight reading. Empty if no truck data (SDK inactive).</summary>
    public float[] TruckSuspDeflection { get; init; } = Array.Empty<float>();

    /// <summary>Which truck wheels belong to a liftable (tag/pusher) axle, and that axle's current
    /// lift state <0;1> - lets the EXT suspension readout tell "this axle reads near-zero because
    /// it's deliberately raised" apart from a genuinely flat/broken reading on a fixed axle.</summary>
    public bool[] TruckWheelLiftable { get; init; } = Array.Empty<bool>();
    public float[] TruckWheelLift { get; init; } = Array.Empty<float>();

    /// <summary>Which truck wheels are driven/steered - the SDK's own classification, not something
    /// the app infers from position. Combined with TruckWheelLiftable, this tells apart a fixed
    /// (always-down) auxiliary "tag" axle - neither driven nor steered nor liftable - from the
    /// ordinary steer/drive axles, so the EXT suspension readout can label an axle by what it
    /// actually is instead of a plain wheel number. Not meaningful for a trailer (every trailer axle
    /// is inherently neither driven nor steered), so this is truck-only.</summary>
    public bool[] TruckWheelPowered { get; init; } = Array.Empty<bool>();
    public bool[] TruckWheelSteerable { get; init; } = Array.Empty<bool>();

    // --- Trailer (first attached trailer only - the SDK supports multi-trailer combos, but a single
    // trailer is by far the common case and this only needs a instrument-cluster-style readout, not
    // full doubles/triples support). All default to "no trailer" values when none is attached.
    public bool TrailerAttached { get; init; }
    public string TrailerName { get; init; } = "";

    public float TrailerBodyWear { get; init; }
    public float TrailerCargoWear { get; init; }
    public float TrailerChassisWear { get; init; }
    public float TrailerWheelsWear { get; init; }

    public float[] TrailerSuspDeflection { get; init; } = Array.Empty<float>();

    /// <summary>Per-wheel lift state <0;1> and ground contact for the trailer - the actual physical
    /// axle state, distinct from TrailerLiftAxleUp/Indicator below (the tractor's own control/lamp for
    /// commanding it).</summary>
    public float[] TrailerWheelLift { get; init; } = Array.Empty<float>();
    public bool[] TrailerWheelOnGround { get; init; } = Array.Empty<bool>();

    /// <summary>Which trailer wheels belong to a liftable axle - same purpose as
    /// TruckWheelLiftable above, for the trailer's own suspension readout.</summary>
    public bool[] TrailerWheelLiftable { get; init; } = Array.Empty<bool>();

    /// <summary>Tractor-side control state for the trailer's lift axle (mirrors LiftAxleUp/HasLiftAxle
    /// above, which are the tractor's own axle).</summary>
    public bool TrailerLiftAxleUp { get; init; }
    public bool TrailerLiftAxleIndicatorOn { get; init; }

    public static TelemetrySnapshot Disconnected { get; } = new();
}
