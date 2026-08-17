using System.Collections.Generic;

namespace TruckHub.Models;

/// <summary>One scanned .scs mod file and its suggested load-order placement.</summary>
public sealed class ModInfo
{
    public string FileName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Author { get; init; } = "";

    /// <summary>Raw category[] values straight from manifest.sii, if any were found.</summary>
    public List<string> RawCategories { get; init; } = new();

    /// <summary>Settable (not init) - an "Open Def" companion mod's tier gets overwritten to match
    /// its paired base mod's tier during pairing, per explicit instruction to always keep a mod
    /// and its Open Def variant together rather than let them classify independently.</summary>
    public ModTier Tier { get; set; } = ModTier.Unsorted;

    /// <summary>1-based position in the overall suggested load order, continuous across all tiers
    /// (not restarting per tier) - shown in the UI so the order reads unambiguously as a numbered
    /// sequence rather than something that could be mistaken for an unordered list. Set by
    /// ModOrderService after sorting, not meaningful before then.</summary>
    public int OrderNumber { get; set; }

    /// <summary>Sort key used to keep an "Open Def" mod adjacent to its base mod regardless of
    /// alphabetical distance between their actual names - both members of a pair share the same
    /// key (the base mod's own display name); unpaired mods just use their own name. See
    /// ModOrderService.PairOpenDefMods.</summary>
    public string PairSortKey { get; set; } = "";

    /// <summary>0 for the base mod, 1 for its Open Def companion - keeps the base first within a
    /// pair once they share the same PairSortKey.</summary>
    public int PairOrder { get; set; }

    /// <summary>True if manifest.sii itself couldn't be read at all (e.g. neither the zip nor the
    /// zlib-scan path found one) - distinct from Tier==Unsorted, which means it WAS read but
    /// couldn't be confidently classified.</summary>
    public bool ManifestUnreadable { get; init; }
}
