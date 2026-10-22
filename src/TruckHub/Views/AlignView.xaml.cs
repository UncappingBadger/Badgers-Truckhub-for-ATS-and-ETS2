using System;
using System.Windows;
using System.Windows.Controls;

namespace TruckHub.Views;

/// <summary>
/// The ALIGN panel's content (camera picture, status caption, readouts), shared between the drawer docked above
/// MainWindow and the standalone AlignWindow it can pop out into - same arrangement as JobLogView.
/// </summary>
public partial class AlignView : UserControl
{
    public event EventHandler? PopOutRequested;

    public AlignView()
    {
        InitializeComponent();
    }

    /// <summary>The popup itself has nothing further to pop out to, so its host hides this button.</summary>
    public void SetPopOutButtonVisible(bool visible) =>
        PopOutButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void PopOut_Click(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);
}
