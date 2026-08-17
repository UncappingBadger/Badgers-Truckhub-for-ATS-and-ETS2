using System;
using System.Collections.Generic;
using System.Linq;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Maps a mod's manifest.sii data onto the user's tiered community load order. manifest.sii's own
/// category[] field is the primary signal where it's unambiguous (sound/physics/trailer all map
/// cleanly); everything else falls back to keyword matching against the display name and filename,
/// based on the real Steam Workshop taxonomy for ATS/ETS2 (Truck Parts sub-categories: Paintjobs,
/// Rims, Tyres, Cabin accessories, etc.) plus real examples the user corrected by hand (LED/light
/// packs, truck tuning, transmissions, map-expansion mods) rather than a purely guessed vocabulary.
///
/// Deliberately returns Unsorted rather than a low-confidence guess when nothing matches - a wrong
/// placement in a load-order-sensitive list is worse than admitting "place this one manually".
/// </summary>
public static class ModClassifier
{
    public static ModTier Classify(string fileName, string displayName, IReadOnlyList<string> rawCategories)
    {
        var haystack = $"{fileName} {displayName}".ToLowerInvariant();
        var categories = rawCategories.Select(c => c.ToLowerInvariant()).ToList();

        // Unambiguous category[] values first - these map cleanly to exactly one tier with no
        // real-world overlap, so they win regardless of what the name/filename say.
        if (categories.Contains("sound")) return ModTier.SoundFrameworks;
        if (categories.Contains("physics")) return ModTier.PhysicsSystems;
        if (categories.Any(c => c is "paint_job" or "paintjob" or "paint job")) return ModTier.PaintJobsAndSkins;
        if (categories.Contains("trailer")) return ModTier.TrailersAndCargo;

        // Map-expansion mods - not part of the user's original 10-tier list at all (added
        // afterward, deliberately sorts before everything else - see ModTier.MapMods). Checked
        // early since "expansion" is specific enough not to collide with the more common
        // truck/accessory keywords below.
        if (ContainsAny(haystack, "expansion", "promods", " map ", "map mod")) return ModTier.MapMods;

        // Keyword rules, most specific first - a mod can be a "truck" by category but a paint job,
        // wheel pack, or interior accessory by what it actually is, so name-based signals are
        // checked ahead of the generic "truck"/"interior" category fallbacks below.
        if (ContainsAny(haystack, "paint", "skin", "livery", "wrap", "chrome shop")) return ModTier.PaintJobsAndSkins;
        if (ContainsAny(haystack, "wheel", "tire", "tyre", "rim", "hub")) return ModTier.WheelAndTirePacks;
        if (ContainsAny(haystack, "cargo", "freight", "trailer")) return ModTier.TrailersAndCargo;
        if (ContainsAny(haystack, "hud", "advisor", "route advisor", "fullscreen map", "navigation", "menu")) return ModTier.UiAndMenus;
        if (ContainsAny(haystack, "economy", "bank", "loan", " xp ", "experience", "garage price", "easy start")) return ModTier.EconomyAndProgression;
        if (ContainsAny(haystack, "weather", "season", "grimes", "skybox", "sky box", " hdr ", "graphics")) return ModTier.GraphicsAndWeather;

        // Transmissions change gear ratios/shift behaviour - a physics change, not a cosmetic one,
        // even though they're often bundled/named like accessory packs (e.g. "Real Eaton Fuller
        // Transmissions CAST Addon").
        if (ContainsAny(haystack, "transmission", "gearbox", "clutch", "suspension")) return ModTier.PhysicsSystems;

        // Interior/cabin/lights/general truck accessories - broadened per explicit user direction
        // to also cover lights (LED packs, light bars) and general tuning packs, since neither had
        // a clean fit anywhere else in the original 10-tier list.
        if (ContainsAny(haystack, "interior", "dashboard", "dash ", "cabin accessor", "cabin clutter")) return ModTier.InteriorAndCabinAccessories;
        if (ContainsAny(haystack, "steering wheel", "gauge")) return ModTier.InteriorAndCabinAccessories;
        if (ContainsAny(haystack, "led", "light bar", "lightbar", "light pack", "lights pack")) return ModTier.InteriorAndCabinAccessories;
        if (ContainsAny(haystack, "tuning", "accessor")) return ModTier.InteriorAndCabinAccessories;

        // Generic category[] fallbacks - "truck"/"lights"/"interior" alone don't say which of
        // several tiers a mod belongs in (see the "mirror" ambiguity noted when this was designed:
        // a physical mirror accessory vs a UI mirror-display toggle can't be told apart from the
        // manifest alone), so these are deliberately last-resort, coarse guesses.
        if (categories.Any(c => c is "lights")) return ModTier.InteriorAndCabinAccessories;
        if (categories.Contains("truck")) return ModTier.StandaloneTrucks;
        if (categories.Any(c => c is "interior")) return ModTier.InteriorAndCabinAccessories;

        return ModTier.Unsorted;
    }

    private static bool ContainsAny(string haystack, params string[] keywords) =>
        keywords.Any(haystack.Contains);
}
