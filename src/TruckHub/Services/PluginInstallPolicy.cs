using System;
using System.IO;

namespace TruckHub.Services;

public enum PluginInstallDecision
{
    /// <summary>Nothing is installed yet: put ours there.</summary>
    InstallNew,
    /// <summary>What is there is a different, older (or unreadable) build: replace it with ours.</summary>
    Replace,
    /// <summary>The installed file is byte-for-byte ours: nothing to do.</summary>
    AlreadyCurrent,
    /// <summary>The installed file is a NEWER build than the one this TruckHub carries: leave it alone.</summary>
    KeepNewer,
}

/// <summary>
/// Decides whether the bundled telemetry plugin may overwrite what is already in a game's plugins folder. It must never
/// downgrade: running an older TruckHub (or a stale copy) after a newer plugin has been installed - including a test build
/// dropped in by hand - must leave the newer plugin alone.
///
/// The plugin DLLs carry no version number, but every one carries the time it was linked in its Windows (PE) header, and
/// that rises with every build, so the builds can be ordered by it: the installed file is kept if it was built AFTER the
/// bundled one. If either time is missing or implausible (a "reproducible build" stores a hash there instead of a date),
/// the answer falls back to the old rule - replace whatever differs - rather than guessing.
/// </summary>
public static class PluginInstallPolicy
{
    private static readonly DateTime EarliestPlausible = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static PluginInstallDecision Decide(string targetPath, byte[] bundled, out DateTime? installedBuiltUtc, out DateTime? bundledBuiltUtc)
    {
        installedBuiltUtc = null;
        bundledBuiltUtc = BuildTimeUtc(bundled);

        if (!File.Exists(targetPath))
        {
            return PluginInstallDecision.InstallNew;
        }

        if (BytesMatchFile(bundled, targetPath))
        {
            return PluginInstallDecision.AlreadyCurrent;
        }

        installedBuiltUtc = BuildTimeUtc(ReadHeader(targetPath));
        return installedBuiltUtc is { } installed && bundledBuiltUtc is { } ours && installed > ours
            ? PluginInstallDecision.KeepNewer
            : PluginInstallDecision.Replace;
    }

    /// <summary>When the DLL was linked, from its PE header; null if it is not a PE file or the stamp is not a plausible date.</summary>
    public static DateTime? BuildTimeUtc(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            return null;
        }

        var peOffset = BitConverter.ToInt32(bytes.Slice(0x3C, 4));
        if (peOffset < 0 || peOffset > bytes.Length - 12
            || bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E' || bytes[peOffset + 2] != 0 || bytes[peOffset + 3] != 0)
        {
            return null;
        }

        var stamp = BitConverter.ToUInt32(bytes.Slice(peOffset + 8, 4));
        var built = DateTime.UnixEpoch.AddSeconds(stamp);
        return built >= EarliestPlausible && built <= DateTime.UtcNow.AddDays(1) ? built : null;
    }

    private static byte[] ReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            return buffer.AsSpan(0, read).ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    public static bool BytesMatchFile(byte[] bytes, string filePath)
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
