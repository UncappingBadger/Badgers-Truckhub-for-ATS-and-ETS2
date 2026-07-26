using System;
using System.IO;
using System.Reflection;

namespace TruckHub.Services;

public sealed record PluginInstallResult(string GameName, bool Installed, string? Error);

/// <summary>
/// Drops the bundled scs-telemetry.dll (embedded in this assembly) into each detected game's
/// plugins folder so telemetry is available without the user ever touching a file browser.
/// </summary>
public static class PluginInstallerService
{
    private const string PluginFileName = "scs-telemetry.dll";
    private const string EmbeddedResourceName = "TruckHub.scs-telemetry.dll";

    public static PluginInstallResult[] EnsureInstalled(string? manualEts2Path = null, string? manualAtsPath = null)
    {
        var installs = GameLocator.FindInstalls(manualEts2Path, manualAtsPath);

        if (installs.Count == 0)
        {
            return Array.Empty<PluginInstallResult>();
        }

        byte[] bundledDllBytes;
        try
        {
            bundledDllBytes = ReadEmbeddedPlugin();
        }
        catch (Exception ex)
        {
            return Array.ConvertAll(installs.ToArray(), i =>
                new PluginInstallResult(i.DisplayName, false, $"Bundled plugin resource could not be read: {ex.Message}"));
        }

        var results = new PluginInstallResult[installs.Count];
        for (var idx = 0; idx < installs.Count; idx++)
        {
            var install = installs[idx];
            try
            {
                var pluginsDir = Path.Combine(install.RootPath, "bin", "win_x64", "plugins");
                Directory.CreateDirectory(pluginsDir);

                var targetPath = Path.Combine(pluginsDir, PluginFileName);

                if (!File.Exists(targetPath) || !BytesMatchFile(bundledDllBytes, targetPath))
                {
                    File.WriteAllBytes(targetPath, bundledDllBytes);
                }

                results[idx] = new PluginInstallResult(install.DisplayName, true, null);
            }
            catch (Exception ex)
            {
                results[idx] = new PluginInstallResult(install.DisplayName, false, ex.Message);
            }
        }

        return results;
    }

    private static byte[] ReadEmbeddedPlugin()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedResourceName}' not found.");
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
