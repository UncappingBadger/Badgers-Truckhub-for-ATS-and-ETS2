using System.ComponentModel;
using System.Runtime.CompilerServices;
using TruckHub.Services;

namespace TruckHub.ViewModels;

/// <summary>
/// Backs the EXT window's own settings dialog - a separate, minimal window/viewmodel rather than
/// adding a tab to the main SettingsWindow/SettingsViewModel, since none of EXT's toggles have
/// anything to do with the main HUD (game paths, gearbox calibration, update checks, etc. all stay
/// on the main one). Same "own in-memory AppSettings copy, save immediately on each edit" pattern as
/// SettingsViewModel - ExtendedGaugesViewModel picks up changes via its own ReloadSettings(), called
/// when this window closes, exactly like MainViewModel.ReloadSettings() after the main Settings
/// window closes.
/// </summary>
public sealed class ExtendedGaugesSettingsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settingsService;
    private readonly Models.AppSettings _settings;

    public ExtendedGaugesSettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Load();
    }

    public bool ShowOilPressure
    {
        get => _settings.ShowExtOilPressure;
        set { _settings.ShowExtOilPressure = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowOilTemp
    {
        get => _settings.ShowExtOilTemp;
        set { _settings.ShowExtOilTemp = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowWaterTemp
    {
        get => _settings.ShowExtWaterTemp;
        set { _settings.ShowExtWaterTemp = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowBattery
    {
        get => _settings.ShowExtBattery;
        set { _settings.ShowExtBattery = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowBrakeAir
    {
        get => _settings.ShowExtBrakeAir;
        set { _settings.ShowExtBrakeAir = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowBrakeTemp
    {
        get => _settings.ShowExtBrakeTemp;
        set { _settings.ShowExtBrakeTemp = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowSuspension
    {
        get => _settings.ShowExtSuspension;
        set { _settings.ShowExtSuspension = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowTrailerAxleStatus
    {
        get => _settings.ShowExtTrailerAxleStatus;
        set { _settings.ShowExtTrailerAxleStatus = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowWear
    {
        get => _settings.ShowExtWear;
        set { _settings.ShowExtWear = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
