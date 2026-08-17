using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TruckHub.Services;

public sealed record ModManifest(string DisplayName, string Author, List<string> Categories);

/// <summary>
/// Reads manifest.sii out of a .scs mod file. Mods built as a plain zip (like our own
/// clutch_priority_fix, or some community mods e.g. Haterbilt) are read directly via ZipArchive.
/// Most others are SCS's own HashFS2 container, which isn't a zip at all - for those, this scans
/// the raw bytes for a zlib stream header and inflates candidates looking for manifest text,
/// same technique already used and proven during the clutch-mod investigation (see
/// reference_ats_scs_modding_gotchas in project memory).
/// </summary>
public static class ModManifestReader
{
    // Manifest.sii is almost always one of the first entries written into a mod archive - capping
    // the raw-scan window keeps this fast even for mods that are hundreds of MB of textures/models,
    // at the cost of occasionally missing an unusually-packaged mod (falls back to "unreadable"
    // rather than a slow multi-minute full-file scan).
    private const int ScanWindowBytes = 20 * 1024 * 1024;

    public static ModManifest? TryRead(string scsFilePath)
    {
        var viaZip = TryReadViaZip(scsFilePath);
        if (viaZip != null)
        {
            return viaZip;
        }

        return TryReadViaRawScan(scsFilePath);
    }

    private static ModManifest? TryReadViaZip(string scsFilePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(scsFilePath);
            var entry = archive.GetEntry("manifest.sii");
            if (entry == null)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return ParseManifestText(reader.ReadToEnd());
        }
        catch
        {
            // Not a valid zip (almost certainly a HashFS2 archive instead) - the raw-scan fallback handles that.
            return null;
        }
    }

    private static ModManifest? TryReadViaRawScan(string scsFilePath)
    {
        try
        {
            using var stream = File.OpenRead(scsFilePath);
            var windowSize = (int)Math.Min(ScanWindowBytes, stream.Length);
            var data = new byte[windowSize];
            var read = stream.Read(data, 0, windowSize);

            for (var i = 0; i < read - 1; i++)
            {
                if (data[i] != 0x78)
                {
                    continue;
                }

                var cmf = data[i];
                var flg = data[i + 1];
                if ((cmf << 8 | flg) % 31 != 0)
                {
                    continue;
                }

                var text = TryInflateAt(data, i);
                if (text != null && text.Contains("mod_package"))
                {
                    var parsed = ParseManifestText(text);
                    if (parsed != null)
                    {
                        return parsed;
                    }
                }
            }
        }
        catch
        {
            // Fall through to null - genuinely unreadable, caller marks ManifestUnreadable.
        }

        return null;
    }

    private static string? TryInflateAt(byte[] data, int offset)
    {
        try
        {
            using var input = new MemoryStream(data, offset, data.Length - offset, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch
        {
            // Most candidate offsets are just incidental 0x78 bytes in binary data, not real zlib
            // streams - a decompression failure here is the expected common case, not a real error.
            return null;
        }
    }

    private static ModManifest? ParseManifestText(string text)
    {
        if (!text.Contains("mod_package"))
        {
            return null;
        }

        var displayName = Regex.Match(text, "display_name\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
        var author = Regex.Match(text, "author\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;

        var categories = new List<string>();
        foreach (Match m in Regex.Matches(text, "category\\[\\]\\s*:\\s*\"([^\"]*)\""))
        {
            categories.Add(m.Groups[1].Value);
        }

        return new ModManifest(displayName, author, categories);
    }
}
