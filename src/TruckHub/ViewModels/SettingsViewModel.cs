using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly AppSettings _settings;

    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Disconnected;
    private string _normalGearNumberInput = "";
    private string _splitterGearNumberInput = "";

    public SettingsViewModel(SettingsService settingsService, TelemetryService telemetryService, GearCaptureService gearCaptureService)
    {
        _settingsService = settingsService;
        _telemetryService = telemetryService;
        _gearCaptureService = gearCaptureService;
        _settings = settingsService.Load();

        _telemetryService.SnapshotUpdated += OnSnapshotUpdated;
        _gearCaptureService.Changed += OnCaptureChanged;

        BrowseEts2Command = new RelayCommand(_ => Browse(SimGame.Ets2, "Euro Truck Simulator 2"));
        ClearEts2Command = new RelayCommand(_ => Clear(SimGame.Ets2));
        BrowseAtsCommand = new RelayCommand(_ => Browse(SimGame.Ats, "American Truck Simulator"));
        ClearAtsCommand = new RelayCommand(_ => Clear(SimGame.Ats));

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
    }

    public ICommand BrowseEts2Command { get; }
    public ICommand ClearEts2Command { get; }
    public ICommand BrowseAtsCommand { get; }
    public ICommand ClearAtsCommand { get; }

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

    /// <summary>Raised after a plugin (re)install attempt so the window hosting this can refresh its main telemetry status.</summary>
    public event Action? PathsChanged;

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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
