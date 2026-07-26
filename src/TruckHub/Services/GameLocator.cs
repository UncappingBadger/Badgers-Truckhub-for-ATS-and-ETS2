using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TruckHub.Models;

namespace TruckHub.Services;

public sealed record GameInstall(SimGame Game, string DisplayName, string RootPath, bool IsManual);

/// <summary>
/// Finds local ETS2/ATS install folders so the plugin DLL can be dropped into
/// "&lt;install&gt;\bin\win_x64\plugins\" without the user having to find the path themselves.
/// Auto-detection covers every Steam install regardless of drive/folder (Steam itself records
/// the real path in the registry), but falls short for non-Steam copies or a disconnected/renamed
/// library drive - callers can supply a manual override per game to cover those cases.
/// </summary>
public static class GameLocator
{
    private static readonly (SimGame Game, string RegistryName, string FolderName, string DisplayName)[] Games =
    {
        (SimGame.Ets2, "Steam App 227300", "Euro Truck Simulator 2", "Euro Truck Simulator 2"),
        (SimGame.Ats, "Steam App 270880", "American Truck Simulator", "American Truck Simulator"),
    };

    /// <summary>A folder "looks like" a valid install if it has the bin\win_x64 layout every ETS2/ATS install has.</summary>
    public static bool LooksLikeValidInstall(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "bin", "win_x64"));

    public static string? FindAutoPath(SimGame game)
    {
        var entry = Array.Find(Games, g => g.Game == game);
        if (entry == default)
        {
            return null;
        }

        var path = FindViaUninstallKey(entry.RegistryName);
        if (path != null && Directory.Exists(path))
        {
            return path;
        }

        foreach (var libraryRoot in FindSteamLibraryFolders())
        {
            var candidate = Path.Combine(libraryRoot, "steamapps", "common", entry.FolderName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Combines auto-detection with manual overrides. A manual path, if set and valid, always wins
    /// for that game over whatever auto-detect finds.
    /// </summary>
    public static List<GameInstall> FindInstalls(string? manualEts2Path = null, string? manualAtsPath = null)
    {
        var manualByGame = new Dictionary<SimGame, string?>
        {
            [SimGame.Ets2] = manualEts2Path,
            [SimGame.Ats] = manualAtsPath,
        };

        var found = new List<GameInstall>();

        foreach (var game in Games)
        {
            var manualPath = manualByGame[game.Game];
            if (!string.IsNullOrWhiteSpace(manualPath) && Directory.Exists(manualPath))
            {
                found.Add(new GameInstall(game.Game, game.DisplayName, manualPath, IsManual: true));
                continue;
            }

            var autoPath = FindAutoPath(game.Game);
            if (autoPath != null)
            {
                found.Add(new GameInstall(game.Game, game.DisplayName, autoPath, IsManual: false));
            }
        }

        return found;
    }

    private static string? FindViaUninstallKey(string steamAppKey)
    {
        string[] roots =
        {
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + steamAppKey,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + steamAppKey,
        };

        foreach (var root in roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            var location = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(location))
            {
                return location;
            }
        }

        return null;
    }

    private static List<string> FindSteamLibraryFolders()
    {
        var libraries = new List<string>();

        var steamPath = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            steamPath = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
        }

        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
        {
            return libraries;
        }

        libraries.Add(steamPath);

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            return libraries;
        }

        try
        {
            var text = File.ReadAllText(vdfPath);
            foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
            {
                var raw = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(raw))
                {
                    libraries.Add(raw);
                }
            }
        }
        catch
        {
            // If the VDF can't be parsed, the registry-based install path is still tried separately.
        }

        return libraries;
    }
}
