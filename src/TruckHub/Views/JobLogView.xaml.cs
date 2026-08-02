using System;
using System.Windows;
using System.Windows.Controls;

namespace TruckHub.Views;

/// <summary>
/// The job log's content (totals + entry list), shared between the docked drawer in MainWindow and
/// the standalone LogWindow it can pop out into - both just embed this and set DataContext to the
/// same MainViewModel, so the log's own bindings don't care which host it's currently living in.
/// </summary>
public partial class JobLogView : UserControl
{
    public event EventHandler? PopOutRequested;

    public JobLogView()
    {
        InitializeComponent();
    }

    /// <summary>The popup itself has nothing further to pop out to, so its host hides this button.</summary>
    public void SetPopOutButtonVisible(bool visible) =>
        PopOutButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void PopOut_Click(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);
}
