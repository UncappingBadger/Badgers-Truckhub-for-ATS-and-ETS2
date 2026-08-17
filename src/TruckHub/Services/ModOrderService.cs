using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TruckHub.Models;

namespace TruckHub.Services;

public sealed record ModOrderScanResult(SimGame Game, string? ModFolderPath, List<ModInfo> Mods);

/// <summary>
/// Scans a game's mod folder and suggests a load order. Runs on demand only (from the Mod Order
/// window's Scan button) - never on app launch, since reading every .scs file's manifest is real
/// disk I/O that shouldn't slow down normal HUD startup.
/// </summary>
public static class ModOrderService
{
    // "_open_def" and "__of" (double underscore, required) are the two naming patterns actually
    // observed across real mods (PM_Light_Pack_Open_Def, LED_Mega_Pack_Open_Def,
    // kenworth.k100e__of). Deliberately not the more generic "_of" alone - that's common enough as
    // an incidental substring to cause false matches.
    private static readonly string[] OpenDefMarkers = { "_open_def", "__of" };

    public static Task<ModOrderScanResult> ScanAsync(SimGame game, string? manualModFolderPath)
    {
        return Task.Run(() =>
        {
            var folder = ModFolderLocator.Resolve(game, manualModFolderPath);
            if (folder == null)
            {
                return new ModOrderScanResult(game, null, new List<ModInfo>());
            }

            var mods = new List<ModInfo>();
            foreach (var file in Directory.EnumerateFiles(folder, "*.scs"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var manifest = ModManifestReader.TryRead(file);

                if (manifest == null)
                {
                    mods.Add(new ModInfo { FileName = fileName, DisplayName = fileName, ManifestUnreadable = true, Tier = ModTier.Unsorted });
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(manifest.DisplayName) ? fileName : manifest.DisplayName;
                var tier = ModClassifier.Classify(fileName, displayName, manifest.Categories);

                mods.Add(new ModInfo
                {
                    FileName = fileName,
                    DisplayName = displayName,
                    Author = manifest.Author,
                    RawCategories = manifest.Categories,
                    Tier = tier,
                });
            }

            foreach (var mod in mods)
            {
                mod.PairSortKey = mod.DisplayName;
            }

            PairOpenDefMods(mods);

            var ordered = mods
                .OrderBy(m => m.Tier == ModTier.Unsorted ? int.MaxValue : (int)m.Tier)
                .ThenBy(m => m.PairSortKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.PairOrder)
                .ThenBy(m => m.DisplayName)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].OrderNumber = i + 1;
            }

            return new ModOrderScanResult(game, folder, ordered);
        });
    }

    /// <summary>
    /// Finds each "Open Def" companion mod's base mod (by filename prefix, after stripping the
    /// Open Def marker) and forces them to share a tier and sort position - per explicit
    /// instruction, a mod and its Open Def variant must never be separated, regardless of what the
    /// classifier would otherwise guess for the companion on its own.
    /// </summary>
    private static void PairOpenDefMods(List<ModInfo> mods)
    {
        foreach (var candidate in mods)
        {
            var marker = OpenDefMarkers.FirstOrDefault(m =>
            {
                var idx = candidate.FileName.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                return idx > 0;
            });
            if (marker == null)
            {
                continue;
            }

            var markerIndex = candidate.FileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var baseNamePrefix = candidate.FileName.Substring(0, markerIndex).TrimEnd('_', ' ', '-', '.');
            if (baseNamePrefix.Length == 0)
            {
                continue;
            }

            var baseMod = mods.FirstOrDefault(m =>
                !ReferenceEquals(m, candidate)
                && !OpenDefMarkers.Any(om => m.FileName.Contains(om, StringComparison.OrdinalIgnoreCase))
                && m.FileName.StartsWith(baseNamePrefix, StringComparison.OrdinalIgnoreCase));

            if (baseMod == null)
            {
                continue;
            }

            candidate.Tier = baseMod.Tier;
            candidate.PairSortKey = baseMod.PairSortKey;
            candidate.PairOrder = 1;
            baseMod.PairOrder = 0;
        }
    }
}
