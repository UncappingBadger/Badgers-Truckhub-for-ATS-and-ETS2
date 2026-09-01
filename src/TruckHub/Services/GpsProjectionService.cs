using System;

namespace TruckHub.Services;

/// <summary>
/// Converts ATS's raw in-game world-space coordinates into real-world longitude/latitude, so a
/// live truck position can be plotted on a standard web map (see GpsMapWindow).
///
/// ATS's world is authored as a Lambert Conformal Conic (LCC) projection - the game engine's own
/// X/Z coordinates ARE already LCC-projected meters, just scaled by a per-map factor. The six
/// constants below are transcribed from ATS's own def/climate.sii game file (SCS Software's own
/// published data - re-derivable independently from the game's files, not copied from any
/// third-party tool's source), and the inverse-LCC math itself is Snyder's standard, public-domain
/// cartographic formulas ("Map Projections: A Working Manual").
/// </summary>
public static class GpsProjectionService
{
    private const double EarthRadiusMeters = 6_370_997.0;
    private const double LengthOfDegree = EarthRadiusMeters * Math.PI / 180.0;

    private const double StandardParallel1Deg = 33.0;
    private const double StandardParallel2Deg = 45.0;
    private const double OriginLatDeg = 39.0;
    private const double OriginLonDeg = -96.0;

    // [Z-factor, X-factor] - ATS's own per-axis scale from game units to LCC-projected meters.
    private const double MapFactorZ = -0.00017706234;
    private const double MapFactorX = 0.000176689948;

    private static readonly double Phi1 = ToRadians(StandardParallel1Deg);
    private static readonly double Phi2 = ToRadians(StandardParallel2Deg);
    private static readonly double Phi0 = ToRadians(OriginLatDeg);
    private static readonly double Lambda0 = ToRadians(OriginLonDeg);

    // Precomputed per Snyder's inverse-LCC formulas - constant for ATS's fixed parallels/origin.
    private static readonly double N = Math.Log(Math.Cos(Phi1) / Math.Cos(Phi2)) /
        Math.Log(Math.Tan(Math.PI / 4 + Phi2 / 2) / Math.Tan(Math.PI / 4 + Phi1 / 2));
    private static readonly double F = Math.Cos(Phi1) * Math.Pow(Math.Tan(Math.PI / 4 + Phi1 / 2), N) / N;
    private static readonly double Rho0 = EarthRadiusMeters * F / Math.Pow(Math.Tan(Math.PI / 4 + Phi0 / 2), N);

    /// <summary>
    /// Converts raw ATS telemetry world coordinates (X, Z - the horizontal ground plane; Y/altitude
    /// isn't part of this projection) into (longitude, latitude) degrees.
    /// Verified round-trip: (gameX: -85672, gameZ: 7870) -> (lon: ~-114.926, lat: ~36.0096), near
    /// Las Vegas, NV.
    /// </summary>
    public static (double Lon, double Lat) ToLonLat(double gameX, double gameZ)
    {
        var lccX = gameX * MapFactorX * LengthOfDegree;
        var lccY = gameZ * MapFactorZ * LengthOfDegree;

        var rho = Math.Sign(N) * Math.Sqrt(lccX * lccX + (Rho0 - lccY) * (Rho0 - lccY));
        var theta = Math.Atan2(lccX, Rho0 - lccY);

        var lat = 2 * Math.Atan(Math.Pow(EarthRadiusMeters * F / rho, 1.0 / N)) - Math.PI / 2;
        var lon = Lambda0 + theta / N;

        return (ToDegrees(lon), ToDegrees(lat));
    }

    /// <summary>
    /// The inverse of ToLonLat - converts a map click's (longitude, latitude) back into raw ATS
    /// world coordinates, for manual pin-placement routing (see GpsMapViewModel.SetManualDestination).
    /// Verified round-trip against the same known case: (lon: -114.92606131601985, lat: 36.00959614633237)
    /// -> (gameX: -85672, gameZ: 7870), matching ToLonLat's own documented example exactly.
    /// </summary>
    public static (double GameX, double GameZ) ToGameCoords(double lon, double lat)
    {
        var lonRad = ToRadians(lon);
        var latRad = ToRadians(lat);

        var rho = EarthRadiusMeters * F / Math.Pow(Math.Tan(Math.PI / 4 + latRad / 2), N);
        var theta = N * (lonRad - Lambda0);
        var lccX = rho * Math.Sin(theta);
        var lccY = Rho0 - rho * Math.Cos(theta);

        var gameX = lccX / (MapFactorX * LengthOfDegree);
        var gameZ = lccY / (MapFactorZ * LengthOfDegree);
        return (gameX, gameZ);
    }

    /// <summary>
    /// Converts the SDK's raw heading (unit range [0,1)=[0,360), 0=north, counterclockwise) into a
    /// standard clockwise-from-north bearing in degrees, for rotating a map marker icon.
    ///
    /// Derived (not guessed) from SCSTelemetry.Methods.cs's own Rotate() matrix: applying it to a
    /// local forward vector of (0,0,-1) at headingUnit 0/0.25/0.5/0.75 gives world-space directions
    /// of -Z/-X/+Z/+X respectively. Cross-referenced against ToLonLat's own axis signs (+gameX ->
    /// east, MapFactorZ negative so -gameZ -> north): that sequence is north/west/south/east - i.e.
    /// bearing = -headingUnit*360, not the previous "+ pi/2" formula, which was live-confirmed wrong
    /// (map marker orientation didn't match the in-game GPS arrow at all, not even the right general
    /// direction).
    /// </summary>
    public static double HeadingToBearingDegrees(float headingUnit)
    {
        var degrees = -headingUnit * 360.0 % 360.0;
        return degrees < 0 ? degrees + 360.0 : degrees;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
