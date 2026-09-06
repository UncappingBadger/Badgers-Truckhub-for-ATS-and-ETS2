using System.Windows;
using System.Windows.Input;
using TruckHub.Services;
using TruckHub.ViewModels;

namespace TruckHub.Views;

public partial class ExtendedGaugesSettingsWindow : Window
{
    public ExtendedGaugesSettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = new ExtendedGaugesSettingsViewModel(settingsService);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
