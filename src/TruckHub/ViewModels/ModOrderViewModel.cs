using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using TruckHub.Models;
using TruckHub.Services;

namespace TruckHub.ViewModels;

public sealed class ModTierGroup
{
    public string TierName { get; init; } = "";
    public List<ModInfo> Mods { get; init; } = new();
}

public sealed class ModOrderViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settingsService;

    private bool _isScanningAts;
    private bool _isScanningEts2;
    private bool _hasScannedAts;
    private bool _hasScannedEts2;

    public ModOrderViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        ScanAtsCommand = new RelayCommand(_ => _ = RunScanAsync(SimGame.Ats), _ => !_isScanningAts);
        ScanEts2Command = new RelayCommand(_ => _ = RunScanAsync(SimGame.Ets2), _ => !_isScanningEts2);
    }

    public ICommand ScanAtsCommand { get; }
    public ICommand ScanEts2Command { get; }

    public bool IsScanningAts
    {
        get => _isScanningAts;
        private set { _isScanningAts = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanAtsButtonText)); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsScanningEts2
    {
        get => _isScanningEts2;
        private set { _isScanningEts2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanEts2ButtonText)); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool HasScannedAts
    {
        get => _hasScannedAts;
        private set { _hasScannedAts = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanAtsButtonText)); }
    }

    public bool HasScannedEts2
    {
        get => _hasScannedEts2;
        private set { _hasScannedEts2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanEts2ButtonText)); }
    }

    public string ScanAtsButtonText => _isScanningAts ? "Scanning..." : _hasScannedAts ? "Rescan" : "Scan";
    public string ScanEts2ButtonText => _isScanningEts2 ? "Scanning..." : _hasScannedEts2 ? "Rescan" : "Scan";

    public List<ModTierGroup> AtsGroups { get; private set; } = new();
    public List<ModTierGroup> Ets2Groups { get; private set; } = new();

    public string? AtsFolderNotFoundMessage { get; private set; }
    public string? Ets2FolderNotFoundMessage { get; private set; }

    // BooleanToVisibilityConverter only recognizes an actual bool - binding it directly to the
    // string message above silently falls through to Collapsed regardless of content, so these
    // exist purely to give the Visibility binding a real bool to look at.
    public bool HasAtsFolderMessage => !string.IsNullOrEmpty(AtsFolderNotFoundMessage);
    public bool HasEts2FolderMessage => !string.IsNullOrEmpty(Ets2FolderNotFoundMessage);

    private async Task RunScanAsync(SimGame game)
    {
        if (game == SimGame.Ats)
        {
            IsScanningAts = true;
        }
        else
        {
            IsScanningEts2 = true;
        }

        try
        {
            var settings = _settingsService.Load();
            var manualPath = game == SimGame.Ats ? settings.ManualAtsModFolderPath : settings.ManualEts2ModFolderPath;
            var result = await ModOrderService.ScanAsync(game, manualPath);

            var groups = BuildGroups(result.Mods);
            var notFoundMessage = result.ModFolderPath == null
                ? $"No {(game == SimGame.Ats ? "ATS" : "ETS2")} mod folder found - set one manually in Settings if it's in a nonstandard location."
                : result.Mods.Count == 0 ? "Mod folder found, but no .scs files are in it." : null;

            if (game == SimGame.Ats)
            {
                AtsGroups = groups;
                AtsFolderNotFoundMessage = notFoundMessage;
                OnPropertyChanged(nameof(AtsGroups));
                OnPropertyChanged(nameof(AtsFolderNotFoundMessage));
                OnPropertyChanged(nameof(HasAtsFolderMessage));
                HasScannedAts = true;
            }
            else
            {
                Ets2Groups = groups;
                Ets2FolderNotFoundMessage = notFoundMessage;
                OnPropertyChanged(nameof(Ets2Groups));
                OnPropertyChanged(nameof(Ets2FolderNotFoundMessage));
                OnPropertyChanged(nameof(HasEts2FolderMessage));
                HasScannedEts2 = true;
            }
        }
        catch (Exception ex)
        {
            // A scan failure should never look like nothing happened - log it and surface it in
            // the same message slot the "not found" case already uses, rather than silently
            // leaving the button reset with no visible result.
            AppLogger.Log($"ModOrderViewModel: scan failed for {game}: {ex}");
            var errorMessage = $"Scan failed: {ex.Message}";

            if (game == SimGame.Ats)
            {
                AtsFolderNotFoundMessage = errorMessage;
                OnPropertyChanged(nameof(AtsFolderNotFoundMessage));
                OnPropertyChanged(nameof(HasAtsFolderMessage));
                HasScannedAts = true;
            }
            else
            {
                Ets2FolderNotFoundMessage = errorMessage;
                OnPropertyChanged(nameof(Ets2FolderNotFoundMessage));
                OnPropertyChanged(nameof(HasEts2FolderMessage));
                HasScannedEts2 = true;
            }
        }
        finally
        {
            if (game == SimGame.Ats)
            {
                IsScanningAts = false;
            }
            else
            {
                IsScanningEts2 = false;
            }
        }
    }

    private static List<ModTierGroup> BuildGroups(List<ModInfo> mods) =>
        mods
            .GroupBy(m => m.Tier)
            .OrderBy(g => g.Key == ModTier.Unsorted ? int.MaxValue : (int)g.Key)
            .Select(g => new ModTierGroup { TierName = g.Key.DisplayName(), Mods = g.ToList() })
            .ToList();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
