using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TruckHub.Models;
using TruckHub.Services;

namespace TruckHub;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // CPU vs GPU drawing is the user's choice (Settings > App Configure > Rendering): the HUD windows
        // and the GPS map are two separate switches. They are read here, once, and fixed for the life of
        // the process - WPF only honours RenderOptions.ProcessRenderMode before ANY window exists (which
        // is why this comes first, ahead of base.OnStartup creating the StartupUri window), and the map's
        // browser argument is fixed when its environment is created, which the GPS pre-warm does shortly
        // after startup. Drawing the HUD on the CPU takes TruckHub's own windows off the GPU so a game
        // that is already maxing the card (and the desktop compositor) can't starve them.
        //   TRUCKHUB_RENDER=software|hardware overrides BOTH switches for diagnostics/A-B testing.
        AppSettings startupSettings;
        try
        {
            startupSettings = new SettingsService().Load();
        }
        catch
        {
            startupSettings = new AppSettings();
        }

        RenderSettings.Initialize(startupSettings, Environment.GetEnvironmentVariable("TRUCKHUB_RENDER"));
        if (RenderSettings.HudSoftware)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        base.OnStartup(e);

        CoreBalancer.Start();
#if DEBUG
        DebugMemoryLog.Start(); // dev builds only: a per-minute managed/native memory line in logs\memdiag.csv
#endif

        AppLogger.Log("TruckHub started");
        AppLogger.Log(
            $"Render mode: HUD windows on {(RenderSettings.HudSoftware ? "CPU (software)" : "GPU")} [{RenderSettings.HudSource}], " +
            $"GPS map on {(RenderSettings.MapSoftware ? "CPU (software)" : "GPU")} [{RenderSettings.MapSource}], " +
            $"WPF render tier {RenderCapability.Tier >> 16}");

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Log($"UNHANDLED UI EXCEPTION: {args.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Log($"UNHANDLED EXCEPTION: {args.ExceptionObject}");
        };

        // Copying the plugin DLL into the game folder(s) never needs to block the UI from showing up.
        Task.Run(() =>
        {
            var settings = new SettingsService().Load();
            foreach (var result in PluginInstallerService.EnsureInstalled(settings.ManualEts2Path, settings.ManualAtsPath))
            {
                AppLogger.Log(result.Installed
                    ? $"Plugin installed for {result.GameName}"
                    : $"Plugin install FAILED for {result.GameName}: {result.Error}");

                if (result.Note != null)
                {
                    AppLogger.Log($"Plugin note for {result.GameName}: {result.Note}");
                }
            }
        });
    }
}
