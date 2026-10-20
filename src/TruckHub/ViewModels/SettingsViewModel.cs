using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using TruckHub.Models;
using TruckHub.Services;

namespace TruckHub.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly TelemetryService _telemetryService;
    private readonly GearCaptureService _gearCaptureService;
    private readonly UpdateCheckService _updateCheckService;
    private readonly AppSettings _settings;

    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Disconnected;
    private string _normalGearNumberInput = "";
    private string _splitterGearNumberInput = "";
    private bool _isCheckingForUpdate;
    private bool _isAppConfigureTab = true;

    public SettingsViewModel(SettingsService settingsService, TelemetryService telemetryService,
        GearCaptureService gearCaptureService, UpdateCheckService updateCheckService)
    {
        _settingsService = settingsService;
        _telemetryService = telemetryService;
        _gearCaptureService = gearCaptureService;
        _updateCheckService = updateCheckService;
        _settings = settingsService.Load();

        _telemetryService.SnapshotUpdated += OnSnapshotUpdated;
        _gearCaptureService.Changed += OnCaptureChanged;
        _updateCheckService.Checked += OnUpdateChecked;

        CheckForUpdatesCommand = new RelayCommand(_ => _ = RunCheckForUpdatesAsync(), _ => !_isCheckingForUpdate);
        OpenUpdateLinkCommand = new RelayCommand(_ =>
        {
            if (_updateCheckService.LastResult?.ReleaseUrl is { } url)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        });

        BrowseEts2Command = new RelayCommand(_ => Browse(SimGame.Ets2, "Euro Truck Simulator 2"));
        ClearEts2Command = new RelayCommand(_ => Clear(SimGame.Ets2));
        BrowseAtsCommand = new RelayCommand(_ => Browse(SimGame.Ats, "American Truck Simulator"));
        ClearAtsCommand = new RelayCommand(_ => Clear(SimGame.Ats));

        BrowseEts2ModFolderCommand = new RelayCommand(_ => BrowseModFolder(SimGame.Ets2, "Euro Truck Simulator 2"));
        ClearEts2ModFolderCommand = new RelayCommand(_ => ClearModFolder(SimGame.Ets2));
        BrowseAtsModFolderCommand = new RelayCommand(_ => BrowseModFolder(SimGame.Ats, "American Truck Simulator"));
        ClearAtsModFolderCommand = new RelayCommand(_ => ClearModFolder(SimGame.Ats));

        SetNormalGearCommand = new RelayCommand(_ => SetGear(splitter: false), _ => CanSetGear(NormalGearNumberInput, wantSplitter: false));
        SetSplitterGearCommand = new RelayCommand(_ => SetGear(splitter: true), _ => CanSetGear(SplitterGearNumberInput, wantSplitter: true));
        ClearCalibrationCommand = new RelayCommand(_ => ClearCalibration(), _ => HasCalibrationForCurrentGearbox);

        ToggleUnitCommand = new RelayCommand(_ =>
        {
            _settings.UseMetric = !_settings.UseMetric;
            _settingsService.Save(_settings);
            OnPropertyChanged(nameof(UnitLabel));
        });

        ToggleWeightUnitCommand = new RelayCommand(_ =>
        {
            _settings.UseImperialWeight = !_settings.UseImperialWeight;
            _settingsService.Save(_settings);
            OnPropertyChanged(nameof(WeightUnitLabel));
        });

        ToggleFuelUnitCommand = new RelayCommand(_ =>
        {
            _settings.UseMetricFuel = !_settings.UseMetricFuel;
            _settingsService.Save(_settings);
            OnPropertyChanged(nameof(FuelUnitLabel));
        });

        SelectAppConfigureTabCommand = new RelayCommand(_ => SetActiveTab(appConfigure: true));
        SelectUiSetupTabCommand = new RelayCommand(_ => SetActiveTab(appConfigure: false));

        PreviewBeepCommand = new RelayCommand(_ => AlertSound.PlayDoubleBeep(OverLimitBeepVolume));
        RestartCommand = new RelayCommand(_ => RestartApp());

        // The two tones overlap if started together (one sound at a time), so the second waits for the first.
        PreviewLimitChangeBeepCommand = new RelayCommand(_ => _ = Task.Run(async () =>
        {
            var volume = SpeedLimitChangeBeepVolume;
            AlertSound.PlayLimitChangeBeep(increased: true, volume);
            await Task.Delay(500);
            AlertSound.PlayLimitChangeBeep(increased: false, volume);
        }));
    }

    public ICommand SelectAppConfigureTabCommand { get; }
    public ICommand SelectUiSetupTabCommand { get; }

    public bool IsAppConfigureTab => _isAppConfigureTab;
    public bool IsUiSetupTab => !_isAppConfigureTab;

    private void SetActiveTab(bool appConfigure)
    {
        _isAppConfigureTab = appConfigure;
        OnPropertyChanged(nameof(IsAppConfigureTab));
        OnPropertyChanged(nameof(IsUiSetupTab));
    }

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand OpenUpdateLinkCommand { get; }

    public ICommand BrowseEts2Command { get; }
    public ICommand ClearEts2Command { get; }
    public ICommand BrowseAtsCommand { get; }
    public ICommand ClearAtsCommand { get; }

    public ICommand BrowseEts2ModFolderCommand { get; }
    public ICommand ClearEts2ModFolderCommand { get; }
    public ICommand BrowseAtsModFolderCommand { get; }
    public ICommand ClearAtsModFolderCommand { get; }

    public ICommand SetNormalGearCommand { get; }
    public ICommand SetSplitterGearCommand { get; }
    public ICommand ClearCalibrationCommand { get; }

    public ICommand ToggleUnitCommand { get; }
    public ICommand ToggleWeightUnitCommand { get; }
    public ICommand ToggleFuelUnitCommand { get; }

    public string UnitLabel => _settings.UseMetric ? "KM/H" : "MPH";
    public string WeightUnitLabel => _settings.UseImperialWeight ? "LBS" : "T";
    public string FuelUnitLabel => _settings.UseMetricFuel ? "L" : "GAL";

    public bool HasManualEts2Path => !string.IsNullOrWhiteSpace(_settings.ManualEts2Path);
    public bool HasManualAtsPath => !string.IsNullOrWhiteSpace(_settings.ManualAtsPath);

    public string Ets2StatusDisplay => DescribeStatus(SimGame.Ets2, _settings.ManualEts2Path);
    public string AtsStatusDisplay => DescribeStatus(SimGame.Ats, _settings.ManualAtsPath);

    public bool HasManualEts2ModFolder => !string.IsNullOrWhiteSpace(_settings.ManualEts2ModFolderPath);
    public bool HasManualAtsModFolder => !string.IsNullOrWhiteSpace(_settings.ManualAtsModFolderPath);

    public string Ets2ModFolderStatusDisplay => DescribeModFolderStatus(SimGame.Ets2, _settings.ManualEts2ModFolderPath);
    public string AtsModFolderStatusDisplay => DescribeModFolderStatus(SimGame.Ats, _settings.ManualAtsModFolderPath);

    /// <summary>Raised after a plugin (re)install attempt so the window hosting this can refresh its main telemetry status.</summary>
    public event Action? PathsChanged;

    // --- Update check ---

    public bool IsCheckingForUpdate => _isCheckingForUpdate;

    public bool HasUpdateAvailable => _updateCheckService.LastResult?.UpdateAvailable ?? false;

    public string? UpdateReleaseUrl => _updateCheckService.LastResult?.ReleaseUrl;

    public string UpdateStatusText
    {
        get
        {
            if (_isCheckingForUpdate)
            {
                return "Checking for updates...";
            }

            var result = _updateCheckService.LastResult;
            if (result == null)
            {
                return "";
            }

            if (!result.CheckSucceeded)
            {
                return "Couldn't check for updates - no internet connection?";
            }

            return result.UpdateAvailable
                ? $"Update available: {result.LatestVersion}"
                : "You're up to date.";
        }
    }

    private async Task RunCheckForUpdatesAsync()
    {
        _isCheckingForUpdate = true;
        OnPropertyChanged(nameof(IsCheckingForUpdate));
        OnPropertyChanged(nameof(UpdateStatusText));
        CommandManager.InvalidateRequerySuggested();

        try
        {
            await _updateCheckService.CheckAsync();
        }
        finally
        {
            _isCheckingForUpdate = false;
            OnPropertyChanged(nameof(IsCheckingForUpdate));
            OnPropertyChanged(nameof(UpdateStatusText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OnUpdateChecked(UpdateCheckResult result)
    {
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(UpdateReleaseUrl));
        CommandManager.InvalidateRequerySuggested();
    }

    // --- Gearbox calibration ---

    public bool IsGearboxConnected => _snapshot.SdkActive && _snapshot.IsHShifter;

    public bool HasSplitter => IsGearboxConnected && _snapshot.SelectorCount >= 2;

    private GearboxCalibration? CurrentCalibration =>
        !string.IsNullOrEmpty(_snapshot.GearboxFingerprint)
            ? _settings.GearboxCalibrations.GetValueOrDefault(_snapshot.GearboxFingerprint)
            : null;

    public bool HasCalibrationForCurrentGearbox => CurrentCalibration != null;

    /// <summary>
    /// The slot/splitter/fingerprint to calibrate against - the hotkey capture if one exists (the
    /// reliable source, since it's taken before any focus loss), otherwise the live snapshot (works
    /// fine for setups where tabbing out doesn't reset the game's shifter state).
    /// </summary>
    private (uint slot, bool splitterHigh, string fingerprint)? ResolveCalibrationSource()
    {
        if (_gearCaptureService.Pending is { } capture)
        {
            return (capture.HShifterSlot, capture.SplitterHigh, capture.GearboxFingerprint);
        }

        if (_snapshot.SdkActive && _snapshot.IsHShifter)
        {
            return (_snapshot.HShifterSlot, _snapshot.SplitterHigh, _snapshot.GearboxFingerprint);
        }

        return null;
    }

    public bool HasPendingCapture => _gearCaptureService.Pending != null;

    public string PendingCaptureDisplay
    {
        get
        {
            if (_gearCaptureService.Pending is not { } capture)
            {
                return "No capture yet. Press Ctrl+Alt+G while driving, in the gear you want to label - " +
                       "it works even with ATS focused, so you don't need to tab out to press it.";
            }

            var age = (int)(DateTime.Now - capture.CapturedAt).TotalSeconds;
            return $"Captured slot {capture.HShifterSlot} ({(capture.SplitterHigh ? "splitter on" : "splitter off")}), {age}s ago. " +
                   "Type its gear number below and click the matching Set button.";
        }
    }

    public string GearboxStatusDisplay
    {
        get
        {
            if (!_snapshot.SdkActive)
            {
                return "Not connected - start driving to calibrate your gearbox.";
            }

            if (!_snapshot.IsHShifter)
            {
                return "Current truck uses an automatic/sequential gearbox - no calibration needed.";
            }

            var calibration = CurrentCalibration;
            if (calibration == null)
            {
                return "Gearbox not calibrated yet.";
            }

            var splitterNote = HasSplitter
                ? calibration.SplitterOffset.HasValue ? ", splitter calibrated" : ", splitter not yet calibrated"
                : "";
            return $"Gearbox calibrated (offset {calibration.NormalOffset}{splitterNote}). " +
                   "Low/High range is automatic - only the splitter needs its own Set.";
        }
    }

    public string NormalGearNumberInput
    {
        get => _normalGearNumberInput;
        set
        {
            _normalGearNumberInput = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SplitterGearNumberInput
    {
        get => _splitterGearNumberInput;
        set
        {
            _splitterGearNumberInput = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool CanSetGear(string input, bool wantSplitter)
    {
        if (!int.TryParse(input, out _))
        {
            return false;
        }

        var source = ResolveCalibrationSource();
        return source.HasValue && source.Value.splitterHigh == wantSplitter;
    }

    private void SetGear(bool splitter)
    {
        var input = splitter ? SplitterGearNumberInput : NormalGearNumberInput;
        if (!int.TryParse(input, out var gearNumber))
        {
            return;
        }

        var source = ResolveCalibrationSource();
        if (source is not { } resolved || resolved.splitterHigh != splitter || string.IsNullOrEmpty(resolved.fingerprint))
        {
            return;
        }

        if (!_settings.GearboxCalibrations.TryGetValue(resolved.fingerprint, out var calibration))
        {
            calibration = new GearboxCalibration();
            _settings.GearboxCalibrations[resolved.fingerprint] = calibration;
        }

        var offset = (int)resolved.slot - gearNumber;
        if (splitter)
        {
            calibration.SplitterOffset = offset;
        }
        else
        {
            calibration.NormalOffset = offset;
        }

        _settingsService.Save(_settings);
        _gearCaptureService.Clear();
        RaiseCalibrationChanged();
    }

    private void ClearCalibration()
    {
        var fingerprint = _snapshot.GearboxFingerprint;
        if (string.IsNullOrEmpty(fingerprint))
        {
            return;
        }

        _settings.GearboxCalibrations.Remove(fingerprint);
        _settingsService.Save(_settings);
        RaiseCalibrationChanged();
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        _snapshot = snapshot;
        RaiseCalibrationChanged();
    }

    private void OnCaptureChanged() => RaiseCalibrationChanged();

    private void RaiseCalibrationChanged()
    {
        OnPropertyChanged(nameof(IsGearboxConnected));
        OnPropertyChanged(nameof(HasSplitter));
        OnPropertyChanged(nameof(HasCalibrationForCurrentGearbox));
        OnPropertyChanged(nameof(GearboxStatusDisplay));
        OnPropertyChanged(nameof(HasPendingCapture));
        OnPropertyChanged(nameof(PendingCaptureDisplay));
        CommandManager.InvalidateRequerySuggested();
    }

    // --- Display component toggles ---

    public bool ShowSpeedSection
    {
        get => _settings.ShowSpeedSection;
        set { _settings.ShowSpeedSection = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowSpeedSign
    {
        get => _settings.ShowSpeedSign;
        set { _settings.ShowSpeedSign = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowCruiseControl
    {
        get => _settings.ShowCruiseControl;
        set { _settings.ShowCruiseControl = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowParkingBrake
    {
        get => _settings.ShowParkingBrake;
        set { _settings.ShowParkingBrake = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowLightsStatus
    {
        get => _settings.ShowLightsStatus;
        set { _settings.ShowLightsStatus = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowGearRpmSection
    {
        get => _settings.ShowGearRpmSection;
        set { _settings.ShowGearRpmSection = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowRoute
    {
        get => _settings.ShowRoute;
        set { _settings.ShowRoute = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowCargo
    {
        get => _settings.ShowCargo;
        set { _settings.ShowCargo = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowPayout
    {
        get => _settings.ShowPayout;
        set { _settings.ShowPayout = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowDistance
    {
        get => _settings.ShowDistance;
        set { _settings.ShowDistance = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowTimeLeft
    {
        get => _settings.ShowTimeLeft;
        set { _settings.ShowTimeLeft = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowFuelSection
    {
        get => _settings.ShowFuelSection;
        set { _settings.ShowFuelSection = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowTripFuelForecast
    {
        get => _settings.ShowTripFuelForecast;
        set { _settings.ShowTripFuelForecast = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowFuelEconomy
    {
        get => _settings.ShowFuelEconomy;
        set { _settings.ShowFuelEconomy = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowAdBlue
    {
        get => _settings.ShowAdBlue;
        set { _settings.ShowAdBlue = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowWarnings
    {
        get => _settings.ShowWarnings;
        set { _settings.ShowWarnings = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool AutoRerouteEnabled
    {
        get => _settings.AutoRerouteEnabled;
        set { _settings.AutoRerouteEnabled = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    // ---- Rendering (App Configure tab): CPU vs GPU drawing, two independent switches, each needing a restart.
    // RenderSettings holds what THIS process started with; RenderRestartNeeded compares the saved choice to it.

    public bool HudSoftwareRendering
    {
        get => _settings.HudSoftwareRendering;
        set
        {
            _settings.HudSoftwareRendering = value;
            _settingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RenderRestartNeeded));
        }
    }

    public bool MapSoftwareRendering
    {
        get => _settings.MapSoftwareRendering;
        set
        {
            _settings.MapSoftwareRendering = value;
            _settingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RenderRestartNeeded));
        }
    }

    /// <summary>True when the saved choice differs from what the running TruckHub started with - shows the restart prompt.</summary>
    public bool RenderRestartNeeded => RenderSettings.RestartNeeded(_settings);

    /// <summary>True when the TRUCKHUB_RENDER environment variable is forcing both switches, so the checkboxes have no effect.</summary>
    public bool RenderOverrideActive => RenderSettings.HudSource != "setting";

    public ICommand RestartCommand { get; }

    private static void RestartApp()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            // If the new copy couldn't be started, don't close this one - the user would be left with nothing.
            AppLogger.Log($"Restart failed: {ex.Message}");
            return;
        }

        System.Windows.Application.Current.Shutdown();
    }

    public bool SpeedLimitChangeBeepEnabled
    {
        get => _settings.SpeedLimitChangeBeepEnabled;
        set { _settings.SpeedLimitChangeBeepEnabled = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    /// <summary>0-100, independent of the over-limit warning's volume. Clamped so a hand-edited settings.json can't push the slider out of range.</summary>
    public int SpeedLimitChangeBeepVolume
    {
        get => Math.Clamp(_settings.SpeedLimitChangeBeepVolume, 0, 100);
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped == _settings.SpeedLimitChangeBeepVolume)
            {
                return;
            }

            _settings.SpeedLimitChangeBeepVolume = clamped;
            _settingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedLimitChangeBeepVolumeLabel));
        }
    }

    public string SpeedLimitChangeBeepVolumeLabel => $"{SpeedLimitChangeBeepVolume}%";

    /// <summary>Plays the limit-up beep then the limit-down beep at this sound's own volume, so both tones can be heard.</summary>
    public ICommand PreviewLimitChangeBeepCommand { get; }

    public bool OverLimitBeepEnabled
    {
        get => _settings.OverLimitBeepEnabled;
        set { _settings.OverLimitBeepEnabled = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    /// <summary>0-100. Clamped here so a hand-edited settings.json can't push the slider out of range.</summary>
    public int OverLimitBeepVolume
    {
        get => Math.Clamp(_settings.OverLimitBeepVolume, 0, 100);
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped == _settings.OverLimitBeepVolume)
            {
                return;
            }

            _settings.OverLimitBeepVolume = clamped;
            _settingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverLimitBeepVolumeLabel));
        }
    }

    public string OverLimitBeepVolumeLabel => $"{OverLimitBeepVolume}%";

    /// <summary>Plays the beep at the slider's current volume so the user can set it by ear.</summary>
    public ICommand PreviewBeepCommand { get; }

    public bool ShowGameClock
    {
        get => _settings.ShowGameClock;
        set { _settings.ShowGameClock = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool ShowOdometer
    {
        get => _settings.ShowOdometer;
        set { _settings.ShowOdometer = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool FatigueSimulationEnabled
    {
        get => _settings.FatigueSimulationEnabled;
        set { _settings.FatigueSimulationEnabled = value; _settingsService.Save(_settings); OnPropertyChanged(); }
    }

    private string DescribeStatus(SimGame game, string? manualPath)
    {
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            return GameLocator.LooksLikeValidInstall(manualPath)
                ? $"Manually set: {manualPath}"
                : $"Manually set (folder doesn't look right): {manualPath}";
        }

        var autoPath = GameLocator.FindAutoPath(game);
        return autoPath != null
            ? $"Found automatically: {autoPath}"
            : "Not found - set the install folder manually below.";
    }

    private void Browse(SimGame game, string title)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = $"Select your {title} install folder",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var path = dialog.SelectedPath;

        if (game == SimGame.Ets2)
        {
            _settings.ManualEts2Path = path;
        }
        else
        {
            _settings.ManualAtsPath = path;
        }

        _settingsService.Save(_settings);
        ReinstallAndRefresh();
    }

    private void Clear(SimGame game)
    {
        if (game == SimGame.Ets2)
        {
            _settings.ManualEts2Path = null;
        }
        else
        {
            _settings.ManualAtsPath = null;
        }

        _settingsService.Save(_settings);
        ReinstallAndRefresh();
    }

    private string DescribeModFolderStatus(SimGame game, string? manualPath)
    {
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            return ModFolderLocator.LooksLikeValidModFolder(manualPath)
                ? $"Manually set: {manualPath}"
                : $"Manually set (folder doesn't look right): {manualPath}";
        }

        var autoPath = ModFolderLocator.FindAutoPath(game);
        return autoPath != null
            ? $"Found automatically: {autoPath}"
            : "Not found - set it manually below if your mod folder is somewhere nonstandard.";
    }

    private void BrowseModFolder(SimGame game, string title)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = $"Select your {title} mod folder",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var path = dialog.SelectedPath;

        if (game == SimGame.Ets2)
        {
            _settings.ManualEts2ModFolderPath = path;
        }
        else
        {
            _settings.ManualAtsModFolderPath = path;
        }

        _settingsService.Save(_settings);
        RaiseModFolderChanged();
    }

    private void ClearModFolder(SimGame game)
    {
        if (game == SimGame.Ets2)
        {
            _settings.ManualEts2ModFolderPath = null;
        }
        else
        {
            _settings.ManualAtsModFolderPath = null;
        }

        _settingsService.Save(_settings);
        RaiseModFolderChanged();
    }

    private void RaiseModFolderChanged()
    {
        OnPropertyChanged(nameof(HasManualEts2ModFolder));
        OnPropertyChanged(nameof(HasManualAtsModFolder));
        OnPropertyChanged(nameof(Ets2ModFolderStatusDisplay));
        OnPropertyChanged(nameof(AtsModFolderStatusDisplay));
    }

    private void ReinstallAndRefresh()
    {
        PluginInstallerService.EnsureInstalled(_settings.ManualEts2Path, _settings.ManualAtsPath);
        RaiseAllChanged();
        PathsChanged?.Invoke();
    }

    private void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(HasManualEts2Path));
        OnPropertyChanged(nameof(HasManualAtsPath));
        OnPropertyChanged(nameof(Ets2StatusDisplay));
        OnPropertyChanged(nameof(AtsStatusDisplay));
    }

    public void Dispose()
    {
        _telemetryService.SnapshotUpdated -= OnSnapshotUpdated;
        _gearCaptureService.Changed -= OnCaptureChanged;
        _updateCheckService.Checked -= OnUpdateChecked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
