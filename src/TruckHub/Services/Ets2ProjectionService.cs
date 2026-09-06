using System;

namespace TruckHub.Services;

/// <summary>
/// ETS2's counterpart to <see cref="GpsProjectionService"/> - same Lambert Conformal Conic math
/// (Snyder's public-domain formulas), but ETS2's own def/climate.sii constants (different standard
/// parallels/origin than ATS), plus one piece ATS doesn't need at all: a UK-specific coordinate
/// rescale. ETS2's UK content is authored at a visibly different in-game scale than the rest of the
/// map (~14.37 vs ~19.15 game units per real-world unit) with no documented reason in any def file -
/// unlike every other constant here, this isn't re-derivable from SCS's own published game data.
/// It's reimplemented clean-room (own structure/naming/comments) from the publicly-documented
/// behavior of `truckermudgeon/maps` (GPL-3, https://github.com/truckermudgeon/maps,
/// packages/libs/map/projections.ts) - the same "informed by public design, not copied code"
/// treatment already applied to this session's routing-algorithm design (see RoutingService.cs).
/// The underlying fact being reproduced (which world-space sector is "UK" and by how much its
/// coordinates need rescaling to land in the right real-world place) is necessarily
/// implementation-identical between any two correct implementations, the same way two independent
/// unit-conversion functions for the same physical constant must agree - there's no room for
/// creative variation in a coordinate transform that has to produce the right answer.
///
/// NOT YET WIRED into any runtime code path - see GpsMapViewModel's GameMapProfile selection
/// (currently ATS-only, ETS2 gated off behind Ets2GpsFeatureFlag.Enabled = false). Written and kept
/// current so flipping that flag on later is a small, low-risk change, but this class itself has
/// never been live-tested against a real ETS2 session - do that before ever setting the flag to
/// true.
/// </summary>
public static class Ets2ProjectionService
{
    private const double EarthRadiusMeters = 6_370_997.0;
    private const double LengthOfDegree = EarthRadiusMeters * Math.PI / 180.0;

    private const double StandardParallel1Deg = 37.0;
    private const double StandardParallel2Deg = 65.0;
    private const double OriginLatDeg = 50.0;
    private const double OriginLonDeg = 15.0;

    // [Z-factor, X-factor] - ETS2's own per-axis scale from game units to LCC-projected meters.
    private const double MapFactorZ = -0.000171570875;
    private const double MapFactorX = 0.0001729241463;

    // Applied to raw game coordinates before everything else - ETS2's world origin isn't at the
    // LCC origin the way ATS's is.
    private const double MapOffsetX = 16660.0;
    private const double MapOffsetZ = 4150.0;

    // The UK rescale - see class comment. Sector size and the "is this sector the UK" test are both
    // in raw (pre-offset) game units; the Calais anchor point is in post-offset units.
    private const double UkScaleFactor = 0.75;
    private const double SectorSizeGameUnits = 4000.0;
    private const double CalaisAnchorX = -31100.0;
    private const double CalaisAnchorZ = -5500.0;

    private static readonly double Phi1 = ToRadians(StandardParallel1Deg);
    private static readonly double Phi2 = ToRadians(StandardParallel2Deg);
    private static readonly double Phi0 = ToRadians(OriginLatDeg);
    private static readonly double Lambda0 = ToRadians(OriginLonDeg);

    private static readonly double N = Math.Log(Math.Cos(Phi1) / Math.Cos(Phi2)) /
        Math.Log(Math.Tan(Math.PI / 4 + Phi2 / 2) / Math.Tan(Math.PI / 4 + Phi1 / 2));
    private static readonly double F = Math.Cos(Phi1) * Math.Pow(Math.Tan(Math.PI / 4 + Phi1 / 2), N) / N;
    private static readonly double Rho0 = EarthRadiusMeters * F / Math.Pow(Math.Tan(Math.PI / 4 + Phi0 / 2), N);

    /// <summary>Converts raw ETS2 telemetry world coordinates (X, Z) into (longitude, latitude)
    /// degrees. Mirrors GpsProjectionService.ToLonLat's shape exactly, plus the UK rescale.</summary>
    public static (double Lon, double Lat) ToLonLat(double gameX, double gameZ)
    {
        var sectorX = Math.Floor(gameX / SectorSizeGameUnits);
        var sectorZ = Math.Floor(gameZ / SectorSizeGameUnits);
        var isUk = IsUkSector(sectorX, sectorZ);

        var x = gameX - MapOffsetX;
        var z = gameZ - MapOffsetZ;
        if (isUk)
        {
            x = (x + CalaisAnchorX / 2) * UkScaleFactor;
            z = (z + CalaisAnchorZ / 2) * UkScaleFactor;
        }

        var lccX = x * MapFactorX * LengthOfDegree;
        var lccY = z * MapFactorZ * LengthOfDegree;

        var rho = Math.Sign(N) * Math.Sqrt(lccX * lccX + (Rho0 - lccY) * (Rho0 - lccY));
        var theta = Math.Atan2(lccX, Rho0 - lccY);

        var lat = 2 * Math.Atan(Math.Pow(EarthRadiusMeters * F / rho, 1.0 / N)) - Math.PI / 2;
        var lon = Lambda0 + theta / N;

        return (ToDegrees(lon), ToDegrees(lat));
    }

    /// <summary>The inverse of ToLonLat - for manual pin-placement clicks on the ETS2 map.</summary>
    public static (double GameX, double GameZ) ToGameCoords(double lon, double lat)
    {
        var lonRad = ToRadians(lon);
        var latRad = ToRadians(lat);

        var rho = EarthRadiusMeters * F / Math.Pow(Math.Tan(Math.PI / 4 + latRad / 2), N);
        var theta = N * (lonRad - Lambda0);
        var lccX = rho * Math.Sin(theta);
        var lccY = Rho0 - rho * Math.Cos(theta);

        var x = lccX / (MapFactorX * LengthOfDegree);
        var z = lccY / (MapFactorZ * LengthOfDegree);

        // Undo the UK rescale by testing the sector the *unscaled* point would fall in - mirrors
        // fromWgs84ToEts2Coords's own forward-then-check ordering, since which sector a point is in
        // can only be evaluated after at least a provisional unscale.
        var provisionalX = x / UkScaleFactor - CalaisAnchorX / 2 + MapOffsetX;
        var provisionalZ = z / UkScaleFactor - CalaisAnchorZ / 2 + MapOffsetZ;
        var isUk = IsUkSector(Math.Floor(provisionalX / SectorSizeGameUnits), Math.Floor(provisionalZ / SectorSizeGameUnits));

        if (isUk)
        {
            x = x / UkScaleFactor - CalaisAnchorX / 2;
            z = z / UkScaleFactor - CalaisAnchorZ / 2;
        }

        return (x + MapOffsetX, z + MapOffsetZ);
    }

    private static bool IsUkSector(double sectorX, double sectorZ) =>
        sectorX <= -8 && sectorZ <= -2 && !(sectorX == -8 && sectorZ == -2);

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
