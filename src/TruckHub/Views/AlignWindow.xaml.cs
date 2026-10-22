using System.Windows;
using System.Windows.Input;
using TruckHub.ViewModels;

namespace TruckHub.Views;

/// <summary>
/// Standalone popped-out ALIGN window - WindowChrome-styled like LogWindow (own title bar, native resize from any
/// edge). Shares the one AlignViewModel with the docked panel; MainWindow decides when it is shown.
/// </summary>
public partial class AlignWindow : Window
{
    public AlignWindow(AlignViewModel viewModel, double left, double top, double width, double height)
    {
        InitializeComponent();

        DataContext = viewModel;
        AlignPanel.SetPopOutButtonVisible(false);

        Left = left;
        Top = top;
        Width = width;
        Height = height;
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
