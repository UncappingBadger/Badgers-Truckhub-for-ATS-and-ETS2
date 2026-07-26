using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TruckHub.Services;
using TruckHub.ViewModels;

namespace TruckHub;

public partial class MainWindow : Window
{
    private const int WM_SYSCOMMAND = 0x112;
    private const int SC_SIZE_BOTTOMRIGHT = 0xF008;

    private const int WM_HOTKEY = 0x0312;
    private const int GearCaptureHotkeyId = 0x4743; // 'GC'
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_G = 0x47;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly SettingsService _settingsService;
    private readonly TelemetryService _telemetryService;
    private readonly GearCaptureService _gearCaptureService;
    private readonly MainViewModel _viewModel;
    private HwndSource? _hwndSource;

    private bool _isFullscreen;
    private double _preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new SettingsService();
        var settings = _settingsService.Load();
        Left = settings.WindowLeft;
        Top = settings.WindowTop;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        _telemetryService = new TelemetryService();
        _gearCaptureService = new GearCaptureService();
        _viewModel = new MainViewModel(_settingsService, _telemetryService);
        DataContext = _viewModel;

        Closed += (_, _) =>
        {
            var current = _settingsService.Load();
            current.WindowLeft = Left;
            current.WindowTop = Top;
            current.WindowWidth = Width;
            current.WindowHeight = Height;
            _settingsService.Save(current);

            if (_hwndSource != null)
            {
                UnregisterHotKey(_hwndSource.Handle, GearCaptureHotkeyId);
                _hwndSource.RemoveHook(WndProc);
            }

            _viewModel.Dispose();
            _telemetryService.Dispose();

            AppLogger.Log("TruckHub closed");
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);

        // Global so it fires even while ATS has focus - ATS resets the H-shifter to neutral the
        // instant its window loses focus, so calibration can't rely on tabbing into Settings and
        // reading the live gear; this captures it beforehand, while the game is still focused.
        if (_hwndSource != null && !RegisterHotKey(_hwndSource.Handle, GearCaptureHotkeyId, MOD_CONTROL | MOD_ALT, VK_G))
        {
            AppLogger.Log("Warning: could not register Ctrl+Alt+G gear-capture hotkey (already in use by another app?)");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == GearCaptureHotkeyId)
        {
            _gearCaptureService.Capture(_telemetryService.LastSnapshot);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Don't hijack clicks meant for a button (unit toggle, close) into a window drag.
        if (IsWithinButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsWithinButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ButtonBase)
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_SIZE_BOTTOMRIGHT, IntPtr.Zero);
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            Left = _preFullscreenLeft;
            Top = _preFullscreenTop;
            Width = _preFullscreenWidth;
            Height = _preFullscreenHeight;
            _isFullscreen = false;
            return;
        }

        _preFullscreenLeft = Left;
        _preFullscreenTop = Top;
        _preFullscreenWidth = Width;
        _preFullscreenHeight = Height;

        // Manual bounds instead of WindowState.Maximized - Maximized has known rendering quirks
        // combined with AllowsTransparency=True on a WindowStyle=None window. Screen.Bounds is in
        // physical pixels, so convert through TransformFromDevice to the DIPs WPF's Left/Top/Width/
        // Height expect, or this overshoots the actual screen on any non-100% display scaling.
        var handle = new WindowInteropHelper(this).Handle;
        var screenBounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        Left = screenBounds.Left * transform.M11;
        Top = screenBounds.Top * transform.M22;
        Width = screenBounds.Width * transform.M11;
        Height = screenBounds.Height * transform.M22;
        _isFullscreen = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new Views.SettingsWindow(_settingsService, _telemetryService, _gearCaptureService) { Owner = this };
        settingsWindow.ShowDialog();
        _viewModel.ReloadSettings();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
