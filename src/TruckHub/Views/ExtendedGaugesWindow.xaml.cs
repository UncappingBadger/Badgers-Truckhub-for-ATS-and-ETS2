using System.Windows;
using System.Windows.Input;
using TruckHub.Services;
using TruckHub.ViewModels;

namespace TruckHub.Views;

/// <summary>
/// EXT tab - extended truck/trailer instrumentation the main dash doesn't show (oil/water/battery/
/// brake gauges, accessory wear, suspension deflection, trailer axle status). WindowChrome-styled
/// like LogWindow, not MainWindow's own AllowsTransparency + manual-resize style - this window has no
/// per-pixel-transparent chrome to justify that approach, so plain native resize is simpler and
/// sidesteps AllowsTransparency's known GPU-compositing weakness under heavy system load.
///
/// Owns its own ExtendedGaugesViewModel bound directly to the shared TelemetryService, the same shape
/// as GpsMapViewModel/JobLogView's own small viewmodels - fully independent of MainViewModel since
/// this window has no need to share its state with the main HUD.
/// </summary>
public partial class ExtendedGaugesWindow : Window
{
    private readonly ExtendedGaugesViewModel _viewModel;
    private readonly SettingsService _settingsService;

    public ExtendedGaugesWindow(TelemetryService telemetryService, SettingsService settingsService, double left, double top)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _viewModel = new ExtendedGaugesViewModel(telemetryService, settingsService);
        DataContext = _viewModel;

        Left = left;
        Top = top;

        Closed += (_, _) => _viewModel.Dispose();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new ExtendedGaugesSettingsWindow(_settingsService) { Owner = this };
        settingsWindow.ShowDialog();
        // Same pattern as MainWindow's own Settings_Click - the child window edits its own
        // in-memory AppSettings copy, so this one has to explicitly re-read from disk afterward.
        _viewModel.ReloadSettings();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
