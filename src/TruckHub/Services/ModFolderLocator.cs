using System;
using System.IO;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Finds each game's mod folder (always under Documents by default, regardless of where the game
/// itself is installed - unrelated to GameLocator, which finds the install folder instead). A
/// manual override covers the rare case of a relocated/symlinked mod folder.
/// </summary>
public static class ModFolderLocator
{
    private static readonly (SimGame Game, string DocumentsFolderName)[] Games =
    {
        (SimGame.Ets2, "Euro Truck Simulator 2"),
        (SimGame.Ats, "American Truck Simulator"),
    };

    public static bool LooksLikeValidModFolder(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public static string? FindAutoPath(SimGame game)
    {
        var entry = Array.Find(Games, g => g.Game == game);
        if (entry == default)
        {
            return null;
        }

        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            entry.DocumentsFolderName, "mod");
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>Manual override wins if set and it actually exists, same convention as GameLocator.</summary>
    public static string? Resolve(SimGame game, string? manualPath) =>
        !string.IsNullOrWhiteSpace(manualPath) && Directory.Exists(manualPath)
            ? manualPath
            : FindAutoPath(game);
}
