using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly UpdateCheckService _updateCheckService;
    private readonly MainViewModel _viewModel;
    private HwndSource? _hwndSource;

    private bool _isFullscreen;
    private double _preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight;

    private const double LogDrawerWidth = 330;
    private enum LogPanelState { Closed, Docked, PoppedOut }
    private LogPanelState _logState = LogPanelState.Closed;
    private Views.LogWindow? _logWindow;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new SettingsService();
        var settings = _settingsService.Load();
        Left = settings.WindowLeft;
        Top = settings.WindowTop;
        // Sanity clamp - a HUD overlay has no legitimate reason to be huge, so a corrupted or
        // fullscreen-sized saved value (see the Closed handler's fullscreen guard) can't leave the
        // app stuck opening at an unusable size on the next launch.
        Width = Math.Clamp(settings.WindowWidth, MinWidth, 1200);
        Height = Math.Clamp(settings.WindowHeight, MinHeight, 900);

        _telemetryService = new TelemetryService();
        _gearCaptureService = new GearCaptureService();
        _updateCheckService = new UpdateCheckService();
        _viewModel = new MainViewModel(_settingsService, _telemetryService, _updateCheckService);
        DataContext = _viewModel;

        Closed += (_, _) =>
        {
            var current = _settingsService.Load();
            // If closed while still in fullscreen (never toggled back), save the size/position from
            // before Fullscreen_Click expanded it, not the fullscreen bounds themselves - otherwise
            // next launch starts fullscreen-sized with no way to tell since _isFullscreen resets to
            // false on a fresh launch.
            var left = _isFullscreen ? _preFullscreenLeft : Left;
            var top = _isFullscreen ? _preFullscreenTop : Top;
            var width = _isFullscreen ? _preFullscreenWidth : Width;
            var height = _isFullscreen ? _preFullscreenHeight : Height;

            current.WindowLeft = left;
            current.WindowTop = top;
            // Don't persist the drawer-expanded width as the "normal" size either - next launch
            // should start with the log closed, same as this session did.
            current.WindowWidth = width - (_logState == LogPanelState.Docked ? LogDrawerWidth : 0);
            current.WindowHeight = height;
            _settingsService.Save(current);

            // A popped-out log is its own window - don't leave it orphaned once the main app closes.
            _logWindow?.Close();

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

    // The arrow tab is a 3-way toggle: Closed -> Docked -> Closed normally, but if the log has been
    // popped out, the same arrow closes the popup instead of re-opening a docked copy alongside it -
    // "the close mechanism stays with the main window" even once the log's living in its own window.
    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        switch (_logState)
        {
            case LogPanelState.Closed:
                SetDockedOpen(true);
                break;
            case LogPanelState.Docked:
                SetDockedOpen(false);
                break;
            case LogPanelState.PoppedOut:
                _logWindow?.Close();
                break;
        }
    }

    private void SetDockedOpen(bool open)
    {
        _logState = open ? LogPanelState.Docked : LogPanelState.Closed;
        LogToggleButton.Content = open ? "‹" : "›";

        // LogDrawerBorder now lives inside the same Viewbox as the card, so it scales along with it
        // while docked - a card zoomed in 2x needs the window to grow by 2x as many actual pixels to
        // reveal the same "220 card-relative units" of drawer, not a flat 220 screen pixels regardless
        // of scale (which either overshot or undershot the actual on-screen drawer size).
        var scale = CardBorder.ActualWidth > 0
            ? (CardBorder.TransformToAncestor(this).Transform(new Point(CardBorder.ActualWidth, 0)).X
               - CardBorder.TransformToAncestor(this).Transform(new Point(0, 0)).X) / CardBorder.ActualWidth
            : 1.0;
        // Small safety margin on top of the measured scale - erring slightly wide (a few extra pixels
        // of letterbox padding) is a far smaller problem than erring narrow (clipping the drawer's own
        // content, which is what actually happened here once before).
        var windowDelta = LogDrawerWidth * scale * 1.1;

        var duration = TimeSpan.FromMilliseconds(220);
        AnimateWidth(LogDrawerBorder, open ? LogDrawerWidth : 0, duration);
        AnimateWidth(this, Width + (open ? windowDelta : -windowDelta), duration);
    }

    // LogWindow's own initial size (see LogWindow.xaml) - used here to keep the pop-out fully
    // on-screen; it's fine that the user can resize it afterward, this is only about where it opens.
    private const double LogWindowInitialWidth = 304;
    private const double LogWindowInitialHeight = 491;

    private void LogView_PopOutRequested(object? sender, EventArgs e)
    {
        // Preferred spot is just to the right of the main window, same as before - but if the main
        // window is sitting near a screen edge, that preferred spot can land partially or entirely
        // off-screen (this bit the user when the main window was docked near their monitor's right
        // edge - the popup opened off-screen and could only be found by dragging the main window
        // back toward the middle first). Flip to the left of the main window if the right doesn't
        // fit, then clamp to the current monitor's working area either way as a final safety net.
        var (popupLeft, popupTop) = ComputePopupPosition();
        SetDockedOpen(false);

        _logWindow = new Views.LogWindow(_viewModel, popupLeft, popupTop);
        _logWindow.Closed += (_, _) =>
        {
            _logWindow = null;
            _logState = LogPanelState.Closed;
            LogToggleButton.Content = "›";
        };
        _logWindow.Show();

        _logState = LogPanelState.PoppedOut;
        LogToggleButton.Content = "‹";
    }

    private (double left, double top) ComputePopupPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var workingArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        // WorkingArea is in physical pixels (like Screen.Bounds in Fullscreen_Click above) - convert
        // through TransformFromDevice to the DIPs Left/Top/Width/Height actually use, or this clamps
        // to the wrong bounds on any non-100% display scaling.
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var screenLeft = workingArea.Left * transform.M11;
        var screenTop = workingArea.Top * transform.M22;
        var screenRight = workingArea.Right * transform.M11;
        var screenBottom = workingArea.Bottom * transform.M22;

        var preferredLeft = Left + Width + 12;
        var left = preferredLeft + LogWindowInitialWidth <= screenRight
            ? preferredLeft
            : Left - LogWindowInitialWidth - 12;

        left = Math.Clamp(left, screenLeft, Math.Max(screenLeft, screenRight - LogWindowInitialWidth));
        var top = Math.Clamp(Top, screenTop, Math.Max(screenTop, screenBottom - LogWindowInitialHeight));
        return (left, top);
    }

    // Animates a FrameworkElement's Width, then hands control back to a plain (non-animated) value at
    // the target - leaving an animation "holding" the property would fight with anything that sets
    // Width directly afterward (the resize grip's native OS resize, or another toggle mid-animation).
    private static void AnimateWidth(FrameworkElement target, double to, TimeSpan duration)
    {
        var from = double.IsNaN(target.Width) ? target.ActualWidth : target.Width;
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed += (_, _) =>
        {
            target.BeginAnimation(WidthProperty, null);
            target.Width = to;
        };
        target.BeginAnimation(WidthProperty, animation);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AcknowledgeUpdate();
        var settingsWindow = new Views.SettingsWindow(_settingsService, _telemetryService, _gearCaptureService, _updateCheckService) { Owner = this };
        settingsWindow.ShowDialog();
        _viewModel.ReloadSettings();
    }

    private void ModOrder_Click(object sender, RoutedEventArgs e)
    {
        var modOrderWindow = new Views.ModOrderWindow(_settingsService) { Owner = this };
        modOrderWindow.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
