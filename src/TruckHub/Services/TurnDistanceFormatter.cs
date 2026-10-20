using System;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Turns a distance measured along the route in the game ENGINE's world metres (raw telemetry X/Z, which is
/// what the route geometry and TurnDetector work in) into the text on the next-turn sign, in the distances
/// the player actually sees. The games shrink their world and run the game clock fast, and show the result on
/// the odometer, the in-game GPS and the Distance card, so an engine distance is scaled up first - otherwise the
/// sign would read a small fraction of what the in-game GPS says for the same turn. The scale is NOT constant
/// (it follows the game's current time acceleration: 3 on slow roads, 15 on UK highways, a constant 16.35 in an ATS
/// convoy; single-player ATS and continental motorways are not measured yet), so callers pass the truck's current measured ratio from <see cref="DisplayScaleEstimator"/>;
/// <see cref="DisplayedPerEngineUnit"/> is only the open-highway fallback.
/// </summary>
public static class TurnDistanceFormatter
{
    /// <summary>Open-highway fallback for displayed metres per engine metre, used only until
    /// <see cref="DisplayScaleEstimator"/> has seen enough driving. These are the games' map scales - the factors
    /// the map projection uses (ATS 19.67, ETS2 19.15) - which are only an open-highway guess (the nominal 1:20 / 1:19). Measured
    /// live in ETS2's UK the real ratio was 15.00 on highways and 3.00 on slow roads, so this is a ceiling, not a
    /// typical value everywhere.</summary>
    public static double DisplayedPerEngineUnit(SimGame game) => game == SimGame.Ets2 ? 19.15 : 19.67;

    /// <summary>
    /// Sign text for a turn <paramref name="engineMeters"/> of route ahead. Metric: whole tens of metres under a
    /// kilometre, then kilometres (one decimal under 10, none from 10 up). Imperial: feet to the nearest 50
    /// under 1,000 ft, then miles (same decimal rule). <paramref name="measuredScale"/> is the truck's current
    /// displayed-per-engine ratio when known (null = use the per-game open-highway fallback).
    /// </summary>
    public static string Format(double engineMeters, SimGame game, bool metric, double? measuredScale = null)
    {
        var scale = measuredScale is > 0 ? measuredScale.Value : DisplayedPerEngineUnit(game);
        var meters = Math.Max(0.0, engineMeters) * scale;

        if (metric)
        {
            if (meters < 1000)
            {
                return $"{Math.Round(meters / 10) * 10:F0} M";
            }

            var km = meters / 1000.0;
            return km < 10 ? $"{km:F1} KM" : $"{km:F0} KM";
        }

        var feet = meters * 3.28084;
        if (feet < 1000)
        {
            return $"{Math.Round(feet / 50) * 50:F0} FT";
        }

        var miles = feet / 5280.0;
        return miles < 10 ? $"{miles:F1} MI" : $"{miles:F0} MI";
    }
}
