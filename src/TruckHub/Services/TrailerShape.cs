using System;
using System.Collections.Generic;
using System.Linq;
using TruckHub.Models;

namespace TruckHub.Services;

public enum TrailerBody
{
    /// <summary>A closed box: dry van, curtain-sider, chip van, livestock, dumper, hopper... - also the fallback for anything unidentified.</summary>
    Box,
    /// <summary>Refrigerated or insulated box.</summary>
    Reefer,
    /// <summary>A flat deck: flatbed, drop deck, lowboy/lowbed, log trailer.</summary>
    Flatbed,
    /// <summary>A cylinder: chemical, food, fuel and gas tankers, silos.</summary>
    Tanker,
    /// <summary>A container chassis carrying an ISO container.</summary>
    Container,
}

/// <summary>
/// What the ALIGN picture draws for the trailer. The telemetry hub (0.1.3 and later) passes on the game's own body type
/// ("dryvan", "flatbed", "chemtank", ...) and the position and radius of every wheel, which gives the real body class and
/// the real axle layout. Older hubs, or a body type this code has not met (a mod's own), fall back to a few keywords in the
/// trailer's name/id and finally to a plain box trailer - "a generic hauler" - rather than a wrong guess.
///
/// The SDK has no trailer length at all; the rear-most axle plus a typical overhang is the closest proxy, so the model's
/// length is an estimate even when the layout is real. U runs along the trailer from the KINGPIN toward the rear (the nose
/// is at U = -NoseAheadM); every dimension is metres.
///
/// The body types the games actually use (read from def.scs of both games, 2026-10-20): dryvan, curtainside, chipvan,
/// bulkfeed, livestock, dumper, hopper, insulated, refrigerated, flatbed, flatbed_brck, dropdeck, lowboy, lowbed, log,
/// container, chemtank, foodtank, fueltank, gastank, silo. Chain types: single, double, b_double/bdouble, hct, rmdouble,
/// tpdouble, triple (only the first trailer of a combination is drawn).
/// </summary>
public sealed record TrailerShape
{
    public TrailerBody Body { get; init; } = TrailerBody.Box;

    /// <summary>Overall length, nose to tail.</summary>
    public double LengthM { get; init; } = 13.6;
    public double WidthM { get; init; } = 2.55;

    /// <summary>How far the front of the trailer sticks out ahead of the kingpin.</summary>
    public double NoseAheadM { get; init; } = 1.15;

    /// <summary>Height of the underside of the body (the kingpin plate sits at about this height) and of its top, above the ground.</summary>
    public double FloorM { get; init; } = 1.25;
    public double TopM { get; init; } = 4.0;

    /// <summary>Distance from the kingpin to each axle, nose to tail. From the game's wheel positions when the hub sends them, otherwise a typical rear group.</summary>
    public IReadOnlyList<double> AxleU { get; init; } = Typical(3, 13.6, 1.15);

    public int Axles => AxleU.Count;

    /// <summary>Sideways position of the wheel centres, and the tyre radius.</summary>
    public double TrackHalfM { get; init; } = 0.9;
    public double WheelRadiusM { get; init; } = 0.52;

    /// <summary>True when the axle layout came from the game's own wheel positions.</summary>
    public bool FromGame { get; init; }

    public static TrailerShape Generic { get; } = new();

    /// <summary>Picks a look from everything the snapshot knows about the trailer.</summary>
    public static TrailerShape From(TelemetrySnapshot s)
    {
        var shape = For(s.Game, s.TrailerName, s.TrailerId, s.TrailerSuspDeflection.Length, s.TrailerBodyType);

        // The real axle layout: wheels sharing a position along the trailer are one axle.
        if (s.TrailerWheelPositions.Length >= 2 && s.TrailerHookPoint is { } kingpin)
        {
            var zs = s.TrailerWheelPositions.Select(p => (double)p.Z).OrderBy(z => z).ToList();
            var axles = new List<double>();
            foreach (var z in zs)
            {
                if (axles.Count == 0 || z - axles[^1] > 0.15)
                {
                    axles.Add(z);
                }
            }

            // z is "back" in trailer space, as is the kingpin's - so distance behind the kingpin is simply the difference.
            var u = axles.Select(z => z - kingpin.Z).Where(x => x > 0.5).ToArray();
            if (u.Length > 0)
            {
                var track = s.TrailerWheelPositions.Max(p => Math.Abs((double)p.X));
                var radius = s.TrailerWheelRadii.Length > 0 ? s.TrailerWheelRadii.OrderBy(r => r).ElementAt(s.TrailerWheelRadii.Length / 2) : shape.WheelRadiusM;
                var length = shape.NoseAheadM + u[^1] + Overhang(shape.Body);
                return shape with
                {
                    AxleU = u,
                    LengthM = Math.Clamp(length, 5.0, 24.0),
                    TrackHalfM = Math.Clamp(track, 0.5, 1.4),
                    WheelRadiusM = Math.Clamp(radius, 0.3, 0.8),
                    FromGame = true,
                };
            }
        }

        return shape;
    }

    /// <summary>The look from the body type (the game's name, or null), the trailer's name/id and the wheel count alone.</summary>
    public static TrailerShape For(SimGame game, string? name, string? id, int wheelCount, string? bodyType = null)
    {
        var ats = game == SimGame.Ats;
        var look = Classify(bodyType) ?? ClassifyByName(((name ?? "") + " " + (id ?? "")).ToLowerInvariant());

        // A tandem is 4 wheel entries, a tri-axle 6 (the games list one entry per wheel position); no data -> the usual three.
        var axles = wheelCount >= 2 ? Math.Clamp(wheelCount / 2, 1, 4) : 3;
        var width = ats ? 2.6 : 2.55;

        var length = look.Body switch
        {
            TrailerBody.Container => 12.2,
            TrailerBody.Tanker => ats ? 14.6 : 12.6,
            TrailerBody.Flatbed => ats ? 14.6 : 13.6,
            _ => ats ? 16.2 : 13.6,
        };

        var topM = look.TopM ?? (look.Body == TrailerBody.Container ? 4.0 : ats ? 4.1 : 4.0);
        return new TrailerShape
        {
            Body = look.Body,
            LengthM = length,
            WidthM = look.Body == TrailerBody.Container ? 2.44 : width,
            FloorM = look.FloorM ?? (look.Body == TrailerBody.Container ? 1.2 : 1.25),
            TopM = topM,
            AxleU = Typical(axles, length, 1.15),
        };
    }

    /// <summary>Rear overhang behind the last axle, by body class.</summary>
    private static double Overhang(TrailerBody body) => body switch
    {
        TrailerBody.Flatbed => 1.0,
        TrailerBody.Container => 0.9,
        TrailerBody.Tanker => 1.2,
        _ => 1.4,
    };

    /// <summary>A typical rear axle group: <paramref name="axles"/> axles 1.4 m apart, the last 1.9 m in from the tail.</summary>
    private static double[] Typical(int axles, double length, double nose)
    {
        var rear = length - nose - 1.9;
        var result = new double[axles];
        for (var i = 0; i < axles; i++)
        {
            result[i] = rear - (axles - 1 - i) * 1.4;
        }

        return result;
    }

    private readonly record struct Look(TrailerBody Body, double? FloorM = null, double? TopM = null);

    /// <summary>The game's own body_type -> a look. Null when the string is empty or not one the games ship.</summary>
    private static Look? Classify(string? bodyType)
    {
        switch ((bodyType ?? "").Trim().ToLowerInvariant())
        {
            case "dryvan":
            case "curtainside":
            case "chipvan":
            case "bulkfeed":
            case "hopper":
            case "livestock":
                return new Look(TrailerBody.Box);
            case "dumper":
                return new Look(TrailerBody.Box, TopM: 3.3);
            case "refrigerated":
            case "insulated":
                return new Look(TrailerBody.Reefer);
            case "flatbed":
            case "flatbed_brck":
                return new Look(TrailerBody.Flatbed, 1.3, 1.5);
            case "dropdeck":
                return new Look(TrailerBody.Flatbed, 1.0, 1.2);
            case "lowboy":
            case "lowbed":
                return new Look(TrailerBody.Flatbed, 0.85, 1.0);
            case "log":
                return new Look(TrailerBody.Flatbed, 1.2, 1.4);
            case "container":
                return new Look(TrailerBody.Container);
            case "chemtank":
            case "foodtank":
            case "fueltank":
            case "gastank":
                return new Look(TrailerBody.Tanker, TopM: 3.5);
            case "silo":
                return new Look(TrailerBody.Tanker, TopM: 3.9);
            default:
                return null;
        }
    }

    /// <summary>Keyword fallback for an older hub (no body type) or a body type from a mod.</summary>
    private static Look ClassifyByName(string key) =>
        Has(key, "container") ? new Look(TrailerBody.Container)
        : Has(key, "tank", "cistern", "silo") ? new Look(TrailerBody.Tanker, TopM: 3.5)
        : Has(key, "flat", "lowbed", "lowloader", "platform") ? new Look(TrailerBody.Flatbed, 1.3, 1.5)
        : Has(key, "reefer", "fridge", "frigo", "coolliner", "refrigerat") ? new Look(TrailerBody.Reefer)
        : new Look(TrailerBody.Box);

    private static bool Has(string key, params string[] words)
    {
        foreach (var w in words)
        {
            if (key.Contains(w, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
