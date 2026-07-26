using System.Windows;
using TruckHub.Services;
using TruckHub.ViewModels;

namespace TruckHub.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsService settingsService, TelemetryService telemetryService, GearCaptureService gearCaptureService)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(settingsService, telemetryService, gearCaptureService);
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
