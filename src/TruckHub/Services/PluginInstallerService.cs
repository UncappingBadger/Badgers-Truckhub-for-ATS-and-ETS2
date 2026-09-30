using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TruckHub.Services;

public sealed record PluginInstallResult(string GameName, bool Installed, string? Error);

/// <summary>
/// Drops the bundled telemetry plugin DLLs (embedded in this assembly) into each detected game's
/// plugins folder so telemetry is available without the user ever touching a file browser.
/// Installs both RenCloud's legacy plugin and TruckHub's own SCS Telemetry Hub plugin (see
/// F:\Claude Projects\ScsTelemetryHub) - they coexist fine side by side. RenCloud's stays installed
/// as a fallback/reference even though TelemetryService.UseScsTelemetryHub is the active source,
/// since flipping that flag back to false (e.g. if the hub ever regresses) must not require a
/// separate install step.
/// </summary>
public static class PluginInstallerService
{
    private static readonly (string FileName, string ResourceName)[] BundledPlugins =
    {
        ("scs-telemetry.dll", "TruckHub.scs-telemetry.dll"),
        ("scs_telemetry_hub.dll", "TruckHub.scs_telemetry_hub.dll"),
    };

    public static PluginInstallResult[] EnsureInstalled(string? manualEts2Path = null, string? manualAtsPath = null)
    {
        var installs = GameLocator.FindInstalls(manualEts2Path, manualAtsPath);

        if (installs.Count == 0)
        {
            return Array.Empty<PluginInstallResult>();
        }

        var bundledPluginBytes = new byte[BundledPlugins.Length][];
        for (var i = 0; i < BundledPlugins.Length; i++)
        {
            try
            {
                bundledPluginBytes[i] = ReadEmbeddedPlugin(BundledPlugins[i].ResourceName);
            }
            catch (Exception ex)
            {
                return Array.ConvertAll(installs.ToArray(), inst =>
                    new PluginInstallResult(inst.DisplayName, false,
                        $"Bundled plugin resource '{BundledPlugins[i].ResourceName}' could not be read: {ex.Message}"));
            }
        }

        var results = new PluginInstallResult[installs.Count];
        for (var idx = 0; idx < installs.Count; idx++)
        {
            var install = installs[idx];
            var pluginsDir = Path.Combine(install.RootPath, "bin", "win_x64", "plugins");
            var errors = new List<string>();

            try
            {
                Directory.CreateDirectory(pluginsDir);

                for (var i = 0; i < BundledPlugins.Length; i++)
                {
                    var targetPath = Path.Combine(pluginsDir, BundledPlugins[i].FileName);
                    var bytes = bundledPluginBytes[i];

                    if (!File.Exists(targetPath) || !BytesMatchFile(bytes, targetPath))
                    {
                        File.WriteAllBytes(targetPath, bytes);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }

            results[idx] = errors.Count == 0
                ? new PluginInstallResult(install.DisplayName, true, null)
                : new PluginInstallResult(install.DisplayName, false, string.Join("; ", errors));
        }

        return results;
    }

    private static byte[] ReadEmbeddedPlugin(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool BytesMatchFile(byte[] bytes, string filePath)
    {
        var info = new FileInfo(filePath);
        if (info.Length != bytes.Length)
        {
            return false;
        }

        var existing = File.ReadAllBytes(filePath);
        return existing.AsSpan().SequenceEqual(bytes);
    }
}
