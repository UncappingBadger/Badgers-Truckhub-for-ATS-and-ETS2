using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TruckHub.Services;

/// <summary>
/// Extracts the GPS map's bundled web assets (HTML/JS/CSS/vendored MapLibre + pre-generated vector
/// tiles - see TruckHub.csproj's Assets\WebMap embed) to a real folder on disk once, so WebView2's
/// SetVirtualHostNameToFolderMapping has an actual directory to serve from. WebView2 can't serve
/// pages directly out of embedded resources or a single-file exe's own bundle, so this is the
/// simplest reliable option - matches the same "extract embedded payload to %LocalAppData% on first
/// use" approach ZoidHub already uses for its own bundled web map.
///
/// An on-demand alternative (reading tiles straight out of the still-zipped archive instead of
/// extracting all 91,859 of them up front) was tried to avoid the first-open extraction delay, but
/// every way of serving that data back to the page - WebResourceRequested interception, a custom
/// MapLibre protocol backed by fetch(), even a postMessage bridge - hit the same wall: fetch() (and,
/// apparently, whatever WebView2 does internally for a vector tile source's own requests) against a
/// SetVirtualHostNameToFolderMapping origin just doesn't work reliably for this content, for reasons
/// that didn't resolve after real effort. Plain disk-extracted files served by WebView2's own default
/// virtual-host handler is the one thing that's actually held up, so that's what this does - the
/// one-time extraction delay is a real, felt cost, but a known and now UI-documented one (see
/// GpsMapWindow's loading overlay) rather than a broken feature.
/// </summary>
public static class WebMapAssetExtractor
{
    private const string ResourcePrefix = "TruckHub.WebMap/";

    private static readonly (string ResourceName, string Subfolder)[] TileZips =
    {
        ("TruckHub.WebMap.tiles.zip", "tiles"),
        ("TruckHub.WebMap.ets2-tiles.zip", "ets2-tiles"),
        // Facility-marker and road-sign overlays - see TruckHub.csproj's own comment on why these
        // are real tiles now instead of the flat road-signs.json/poi-facilities.json GeoJSON this
        // replaced. Nested subfolders (Directory.CreateDirectory below handles the intermediate
        // "tiles" segment fine) so the URL scheme lines up with style.json/gpsmap.js's own
        // TILES_DIR/{overlay}/{z}/{x}/{y}.pbf expectation.
        ("TruckHub.WebMap.poi-tiles.zip", "tiles/poi-facilities"),
        ("TruckHub.WebMap.signs-tiles.zip", "tiles/road-signs"),
        ("TruckHub.WebMap.ets2-poi-tiles.zip", "ets2-tiles/poi-facilities"),
        ("TruckHub.WebMap.ets2-signs-tiles.zip", "ets2-tiles/road-signs"),
        ("TruckHub.WebMap.company-tiles.zip", "tiles/company-logos"),
        ("TruckHub.WebMap.ets2-company-tiles.zip", "ets2-tiles/company-logos"),
        // Flat <token>.png files (not z/x/y tiles) - reuses this same extraction loop since it's
        // just "unzip this archive into this subfolder" either way. One shared bundle for both
        // games (company tokens never collide between them), served at truckhub.app/company-logos/
        // alongside everything else in this same extracted directory.
        ("TruckHub.WebMap.company-logos.zip", "company-logos"),
    };

    /// <summary>Extracts (or re-extracts, if the embedded content has changed since last time) the
    /// web assets and returns the folder they now live in.</summary>
    public static string EnsureExtracted()
    {
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TruckHub-Dev", "WebMap");

        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();

        // Cheap "did anything change" check: name + byte length per resource, hashed together.
        // Resource *count* alone isn't enough - editing an existing file (e.g. adding a button to
        // index.html) doesn't add or remove a resource, so a plain count comparison missed it and
        // kept serving the stale extracted copy. Length only reads embedded-resource stream
        // metadata, not the actual bytes, so this stays cheap even with tiles.zip in the mix.
        var versionMarkerPath = Path.Combine(targetDir, ".extracted-version");
        var currentVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", resourceNames.OrderBy(n => n, StringComparer.Ordinal).Select(n =>
            {
                using var s = assembly.GetManifestResourceStream(n);
                return $"{n}:{s!.Length}";
            })))));
        if (File.Exists(versionMarkerPath) && File.ReadAllText(versionMarkerPath) == currentVersion)
        {
            return targetDir;
        }

        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.CreateDirectory(targetDir);

        foreach (var resourceName in resourceNames)
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = resourceName[ResourcePrefix.Length..]
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var outputPath = Path.Combine(targetDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            using var resourceStream = assembly.GetManifestResourceStream(resourceName)!;
            using var fileStream = File.Create(outputPath);
            resourceStream.CopyTo(fileStream);
        }

        // The vector tiles are bundled separately as a single zip per game (see TruckHub.csproj)
        // rather than individual manifest resources - unpack each into the folder its map style's
        // tile URLs expect (https://truckhub.app/tiles/{z}/{x}/{y}.pbf, or ets2-tiles/... for the
        // not-yet-active ETS2 profile - see GameMapProfile). Extracting both unconditionally, not
        // just whichever game is active, keeps this method's signature simple (no game parameter
        // needed) - the ETS2 entry just sits unused on disk until GameMapProfile.Ets2Enabled flips.
        foreach (var (resourceName, subfolder) in TileZips)
        {
            using var tilesZipStream = assembly.GetManifestResourceStream(resourceName);
            if (tilesZipStream == null)
            {
                continue;
            }

            var tilesDir = Path.Combine(targetDir, subfolder);
            Directory.CreateDirectory(tilesDir);
            using var archive = new ZipArchive(tilesZipStream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(tilesDir);
        }

        File.WriteAllText(versionMarkerPath, currentVersion);
        return targetDir;
    }
}
