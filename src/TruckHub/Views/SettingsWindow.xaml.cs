using System.Windows;
using System.Windows.Input;
using TruckHub.Services;
using TruckHub.ViewModels;

namespace TruckHub.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsService settingsService, TelemetryService telemetryService,
        GearCaptureService gearCaptureService, UpdateCheckService updateCheckService)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(settingsService, telemetryService, gearCaptureService, updateCheckService);
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
