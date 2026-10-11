using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TruckHub.Services;

/// <param name="Note">Informational, not an error: e.g. a plugin file left alone because another tool owns it.</param>
public sealed record PluginInstallResult(string GameName, bool Installed, string? Error, string? Note = null);

/// <summary>
/// Drops the bundled telemetry plugin DLLs (embedded in this assembly) into each detected game's
/// plugins folder so telemetry is available without the user ever touching a file browser.
/// Installs both RenCloud's legacy plugin and TruckHub's own SCS Telemetry Hub plugin (see
/// F:\Claude Projects\ScsTelemetryHub) - they coexist fine side by side. RenCloud's stays installed
/// as a fallback/reference even though TelemetryService.UseScsTelemetryHub is the active source,
/// since flipping that flag back to false (e.g. if the hub ever regresses) must not require a
/// separate install step.
///
/// Another tool may own one of those files: a custom build of scs-telemetry.dll installed with a
/// sidecar manifest (a *.json in the plugins folder whose "TargetFileName" names the DLL) is left
/// strictly alone - never overwritten, never created. Overwriting it would silently swap the
/// user's build for ours on every launch. The hub plugin is TruckHub's own and unaffected.
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
            var notes = new List<string>();

            try
            {
                Directory.CreateDirectory(pluginsDir);

                for (var i = 0; i < BundledPlugins.Length; i++)
                {
                    var fileName = BundledPlugins[i].FileName;
                    var targetPath = Path.Combine(pluginsDir, fileName);
                    var bytes = bundledPluginBytes[i];

                    var owner = FindThirdPartyOwner(pluginsDir, fileName);
                    if (owner != null)
                    {
                        notes.Add($"{fileName} left untouched ({owner} manages it)");
                        continue;
                    }

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

            var note = notes.Count == 0 ? null : string.Join("; ", notes);
            results[idx] = errors.Count == 0
                ? new PluginInstallResult(install.DisplayName, true, null, note)
                : new PluginInstallResult(install.DisplayName, false, string.Join("; ", errors), note);
        }

        return results;
    }

    /// <summary>
    /// Returns a display name for whichever tool has claimed <paramref name="fileName"/> via a sidecar
    /// manifest in the plugins folder, or null if nobody has. A manifest is any *.json whose root
    /// object has a "TargetFileName" equal to the file name (case-insensitive); "Component" supplies
    /// the display name when present. The claim holds even if the DLL itself is currently missing -
    /// writing ours in its place would break the owner's installed-hash bookkeeping. Anything that
    /// isn't a readable manifest (other plugins' configs, malformed JSON) is ignored.
    /// </summary>
    private static string? FindThirdPartyOwner(string pluginsDir, string fileName)
    {
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(pluginsDir, "*.json");
        }
        catch
        {
            return null;
        }

        foreach (var path in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("TargetFileName", out var target) ||
                    target.ValueKind != JsonValueKind.String ||
                    !string.Equals(target.GetString(), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (root.TryGetProperty("Component", out var component) &&
                    component.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(component.GetString()))
                {
                    return component.GetString();
                }

                return Path.GetFileName(path);
            }
            catch
            {
                // not a manifest we can read - keep looking
            }
        }

        return null;
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
