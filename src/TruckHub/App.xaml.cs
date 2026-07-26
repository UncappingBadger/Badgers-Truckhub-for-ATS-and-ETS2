using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TruckHub.Services;

namespace TruckHub;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLogger.Log("TruckHub started");

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
            }
        });
    }
}
