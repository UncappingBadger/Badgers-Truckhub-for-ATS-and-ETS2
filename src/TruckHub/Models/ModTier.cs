namespace TruckHub.Models;

/// <summary>
/// The community-recommended ATS/ETS2 mod load order (top loads first). Sourced from the user's
/// own list, not invented - see ModClassifier for how a mod's manifest/name maps to one of these.
/// MapMods was added after the fact (the original 10-tier list had no slot for map mods at all) -
/// the user confirmed maps should load before everything else, matching common community guidance
/// since so much else can reference map data.
/// </summary>
public enum ModTier
{
    /// <summary>Couldn't confidently classify - shown separately rather than guessed into a tier,
    /// since a wrong guess is worse than admitting uncertainty for something load-order-sensitive.</summary>
    Unsorted = -1,

    MapMods = 0,
    UiAndMenus = 1,
    EconomyAndProgression = 2,
    SoundFrameworks = 3,
    GraphicsAndWeather = 4,
    PhysicsSystems = 5,

    /// <summary>Broadened beyond just cab interior, per the user's explicit call - also covers
    /// general truck accessories/tuning packs and lights (LED packs, light bars), since neither
    /// had a clean fit anywhere else in the original 10-tier list.</summary>
    InteriorAndCabinAccessories = 6,
    StandaloneTrucks = 7,
    PaintJobsAndSkins = 8,
    WheelAndTirePacks = 9,
    TrailersAndCargo = 10,
}

public static class ModTierInfo
{
    public static string DisplayName(this ModTier tier) => tier switch
    {
        ModTier.MapMods => "Map Mods (loads first)",
        ModTier.UiAndMenus => "UI & Menu Layouts",
        ModTier.EconomyAndProgression => "Economy & Progression",
        ModTier.SoundFrameworks => "Sound Frameworks",
        ModTier.GraphicsAndWeather => "Graphics & Season Weather",
        ModTier.PhysicsSystems => "Physics Systems",
        ModTier.InteriorAndCabinAccessories => "Interior & Cabin Accessories (incl. lights, tuning)",
        ModTier.StandaloneTrucks => "Standalone Trucks",
        ModTier.PaintJobsAndSkins => "Paint Jobs & Skins",
        ModTier.WheelAndTirePacks => "Wheel & Tire Packs",
        ModTier.TrailersAndCargo => "Trailers & Cargo Add-ons",
        ModTier.Unsorted => "Unsorted - place manually",
        _ => tier.ToString(),
    };
}
