using System.Windows;
using System.Windows.Input;
using TruckHub.ViewModels;

namespace TruckHub.Views;

/// <summary>
/// Standalone popped-out job log - WindowChrome-styled to hide the native title bar/buttons (see
/// LogWindow.xaml) while keeping real native resize-from-any-edge, rather than the main HUD's fully
/// custom borderless/manually-resized style.
/// </summary>
public partial class LogWindow : Window
{
    public LogWindow(MainViewModel viewModel, double left, double top)
    {
        InitializeComponent();

        DataContext = viewModel;
        LogView.SetPopOutButtonVisible(false);

        Left = left;
        Top = top;
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
