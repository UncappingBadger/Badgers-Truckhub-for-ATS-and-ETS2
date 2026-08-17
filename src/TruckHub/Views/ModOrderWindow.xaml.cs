using System.Windows;
using System.Windows.Input;
using TruckHub.Services;
using TruckHub.ViewModels;

namespace TruckHub.Views;

public partial class ModOrderWindow : Window
{
    public ModOrderWindow(SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = new ModOrderViewModel(settingsService);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
