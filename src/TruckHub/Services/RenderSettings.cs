using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Which parts of TruckHub draw on the GPU and which on the CPU, decided ONCE at startup and fixed for
/// the life of the process. Two independent switches, because they are two different drawing systems:
///   - the HUD windows (main dash, EXT gauges, log, settings): WPF, switched by
///     RenderOptions.ProcessRenderMode, which Windows only honours before the first window exists;
///   - the GPS map: a WebView2 (Chromium) browser in its own processes, switched by a browser startup
///     argument that is fixed when its environment is created (which happens at startup - see
///     GpsCoordinator.Prewarm).
/// Both therefore need a restart to change, so they are captured here rather than re-read per use.
/// </summary>
public static class RenderSettings
{
    /// <summary>Chromium argument that turns the browser's GPU acceleration off. Tested against WebView2
    /// 155: WebGL (which the map needs) still works, on Windows' own software rasterizer (WARP).</summary>
    public const string MapSoftwareBrowserArguments = "--disable-gpu";

    /// <summary>true = the HUD windows are drawn by the CPU (WPF software rendering).</summary>
    public static bool HudSoftware { get; private set; }

    /// <summary>true = the GPS map's browser runs without GPU acceleration.</summary>
    public static bool MapSoftware { get; private set; }

    /// <summary>Where the effective value came from: "setting" or "TRUCKHUB_RENDER override". Logged at startup.</summary>
    public static string HudSource { get; private set; } = "setting";
    public static string MapSource { get; private set; } = "setting";

    /// <summary>The saved settings as they were when this process started - what "restart needed" compares
    /// against (so it stays correct even while an environment-variable override is in force).</summary>
    public static bool HudSettingAtStartup { get; private set; }
    public static bool MapSettingAtStartup { get; private set; }

    /// <summary>Browser arguments for the map's WebView2 environment, or null for the normal GPU path.</summary>
    public static string? MapBrowserArguments => MapSoftware ? MapSoftwareBrowserArguments : null;

    /// <summary>Call once, first thing at startup, before any window exists.</summary>
    public static void Initialize(AppSettings settings, string? environmentOverride)
    {
        HudSettingAtStartup = settings.HudSoftwareRendering;
        MapSettingAtStartup = settings.MapSoftwareRendering;
        (HudSoftware, HudSource) = Resolve(settings.HudSoftwareRendering, environmentOverride);
        (MapSoftware, MapSource) = Resolve(settings.MapSoftwareRendering, environmentOverride);
    }

    /// <summary>
    /// TRUCKHUB_RENDER=software|hardware is a diagnostic override that forces BOTH switches one way (handy
    /// for A/B testing without touching the saved settings); anything else, or unset, means "use the setting".
    /// </summary>
    public static (bool Software, string Source) Resolve(bool settingValue, string? environmentOverride)
    {
        var value = (environmentOverride ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "software" => (true, "TRUCKHUB_RENDER override"),
            "hardware" => (false, "TRUCKHUB_RENDER override"),
            _ => (settingValue, "setting"),
        };
    }

    /// <summary>True when the saved choice differs from what this running process started with.</summary>
    public static bool RestartNeeded(AppSettings saved) =>
        saved.HudSoftwareRendering != HudSettingAtStartup || saved.MapSoftwareRendering != MapSettingAtStartup;
}
