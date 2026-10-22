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
    // Reroute hotkey: Ctrl+Alt+F8. Chosen because F8 is the one key NEITHER game binds anywhere (checked
    // across every ATS/ETS2 profile's controls) - every letter is bound to something, and a plain bound key
    // (e.g. R = radio/photo reset) could also fire in-game when read raw while Ctrl+Alt are held.
    private const int RerouteHotkeyId = 0x5252; // 'RR'
    private const uint VK_F8 = 0x77;
    // ALIGN hotkey: Ctrl+Alt+. (period). Picked by scanning every ATS/ETS2 profile's controls (3 profiles x several game
    // versions): neither game binds comma, period, minus, slash, backslash or F8 on ANY profile, and F8 is already the
    // reroute key above. Every letter is bound by the games, and a bound key could also fire in-game when read raw
    // while Ctrl+Alt are held. Period is a layout-stable virtual key (VK_OEM_PERIOD) on Latin keyboards, and no
    // Windows or common overlay shortcut uses Ctrl+Alt+. - Settings can switch the hotkey off if another tool has it.
    private const int AlignHotkeyId = 0x414C; // 'AL'
    private const uint VK_OEM_PERIOD = 0xBE;

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
    private Views.GpsMapWindow? _gpsMapWindow;
    private Views.ExtendedGaugesWindow? _extendedGaugesWindow;
    private readonly GpsCoordinator _gpsCoordinator;

    // ALIGN (hitch alignment): a drawer docked ABOVE the card, or popped out into its own window - the same three
    // states as the job log, plus the global hotkey. Explicit request for sizing: "a default size comparable to the main
    // window, then shrink 50%" - the drawer is as wide as the card (it sits on it) and half as tall; the pop-out opens
    // at half the main window's own size.
    private enum AlignPanelState { Closed, Docked, PoppedOut }
    private AlignPanelState _alignState = AlignPanelState.Closed;
    private Views.AlignWindow? _alignWindow;
    private readonly AlignViewModel _alignViewModel;
    private bool _alignHotkeyRegistered;
    private double _alignDrawerUnits;   // the drawer's open height in card units (incl. the gap above the card)
    private double _alignWindowGrowth;  // pixels the window was made taller while docked (undone on close / not saved)
    private double _alignTopShift;      // pixels the window was moved up to make room (undone on close / not saved)
    private const double AlignDrawerFraction = 0.5, AlignDrawerMinUnits = 200, AlignDrawerGapUnits = 4;
    private const double AlignPopoutScale = 0.5, AlignPopoutMinWidth = 360, AlignPopoutMinHeight = 240;

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
        // Read fresh each call (not captured once) so a mid-session MPH/KM change in Settings takes
        // effect on the next turn-distance update, same as everywhere else that setting is read.
        _gpsCoordinator = new GpsCoordinator(_telemetryService, () => _settingsService.Load().UseMetric, () => _settingsService.Load().AutoRerouteEnabled);
        // Kicked off unconditionally at startup, not just for users who've used GPS before - see
        // GpsCoordinator's own comment for the "why" (a deliberate trade of a little idle
        // background cost for a much faster first GPS-window open).
        _gpsCoordinator.Prewarm();
        _gearCaptureService = new GearCaptureService();
        _updateCheckService = new UpdateCheckService();
        _viewModel = new MainViewModel(_settingsService, _telemetryService, _updateCheckService);
        DataContext = _viewModel;

        // ALIGN has its own small view model, fed straight from the shared telemetry (the same shape as the GPS and
        // EXT windows) and shared by the docked drawer and the pop-out. UseMetric is read live from the main view
        // model so a unit change applies at once.
        _alignViewModel = new AlignViewModel(() => _viewModel.UseMetric, new SteeringCalibrationFileStore(), new SoundPlayerBeepOutput());
        ApplyAlignBeepSettings();
        _telemetryService.SnapshotUpdated += _alignViewModel.ApplySnapshot;
        AlignPanel.DataContext = _alignViewModel;

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
            // A docked ALIGN drawer made the window taller and moved it up; next launch starts with it closed.
            current.WindowTop = top + (_alignState == AlignPanelState.Docked ? _alignTopShift : 0);
            // Don't persist the drawer-expanded width as the "normal" size either - next launch
            // should start with the log closed, same as this session did.
            current.WindowWidth = width - (_logState == LogPanelState.Docked ? LogDrawerWidth : 0);
            current.WindowHeight = height - (_alignState == AlignPanelState.Docked ? _alignWindowGrowth : 0);
            _settingsService.Save(current);

            // A popped-out log is its own window - don't leave it orphaned once the main app closes.
            _logWindow?.Close();
            _gpsMapWindow?.Close();
            _extendedGaugesWindow?.Close();
            _alignWindow?.Close();
            // GpsMapWindow closing above only releases the coordinator's own "window is open" flag
            // - LAN Mode (if active) is deliberately designed to survive that. The whole app exiting
            // is the actual point everything really has to stop.
            _gpsCoordinator.Shutdown();

            if (_hwndSource != null)
            {
                UnregisterHotKey(_hwndSource.Handle, GearCaptureHotkeyId);
                UnregisterHotKey(_hwndSource.Handle, RerouteHotkeyId);
                UnregisterHotKey(_hwndSource.Handle, AlignHotkeyId);
                _hwndSource.RemoveHook(WndProc);
            }

            _telemetryService.SnapshotUpdated -= _alignViewModel.ApplySnapshot;
            _alignViewModel.Dispose();
            _viewModel.Dispose();
            _telemetryService.Dispose();

            AppLogger.Log("TruckHub closed");
        };

        // Dev convenience for iterating on the GPS map specifically - `TruckHub.exe --gps` opens it
        // straight away instead of needing a manual click every relaunch. Deferred to Loaded rather
        // than fired here directly - GpsMap_Click's popup-position math needs this window's own
        // real position/size already resolved, which isn't settled yet mid-constructor.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--gps") >= 0)
        {
            Loaded += (_, _) => GpsMap_Click(this, new RoutedEventArgs());
        }
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

        // Global too, so it works from inside the game: asks the GPS to recompute the route from where the
        // truck is now. Harmless if the GPS has never been opened or there is no destination.
        if (_hwndSource != null && !RegisterHotKey(_hwndSource.Handle, RerouteHotkeyId, MOD_CONTROL | MOD_ALT, VK_F8))
        {
            AppLogger.Log("Warning: could not register Ctrl+Alt+F8 reroute hotkey (already in use by another app?) - the map's Reroute button still works");
        }

        ApplyAlignHotkeySetting();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == GearCaptureHotkeyId)
        {
            _gearCaptureService.Capture(_telemetryService.LastSnapshot);
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == RerouteHotkeyId)
        {
            _gpsCoordinator.RequestReroute();
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == AlignHotkeyId)
        {
            // Same as clicking the ALIGN tab: open it docked, close it, or close the pop-out.
            ToggleAlign_Click(this, new RoutedEventArgs());
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
        var (popupLeft, popupTop) = ComputePopupPosition(LogWindowInitialWidth, LogWindowInitialHeight);
        SetDockedOpen(false);

        _logWindow = new Views.LogWindow(_viewModel, popupLeft, popupTop);
        _logWindow.Closed += (_, _) =>
        {
            _logWindow = null;
            _logState = LogPanelState.Closed;
        };
        _logWindow.Show();

        _logState = LogPanelState.PoppedOut;
    }

    private const double GpsMapWindowInitialWidth = 900;
    private const double GpsMapWindowInitialHeight = 650;

    private void GpsMap_Click(object sender, RoutedEventArgs e)
    {
        if (_gpsMapWindow != null)
        {
            _gpsMapWindow.Activate();
            return;
        }

        var (left, top) = ComputePopupPosition(GpsMapWindowInitialWidth, GpsMapWindowInitialHeight);
        _gpsMapWindow = new Views.GpsMapWindow(_gpsCoordinator, left, top);
        _gpsMapWindow.Closed += (_, _) => _gpsMapWindow = null;
        _gpsMapWindow.Show();
    }

    private const double ExtendedGaugesWindowInitialWidth = 620;
    private const double ExtendedGaugesWindowInitialHeight = 560;

    private void ExtendedGauges_Click(object sender, RoutedEventArgs e)
    {
        if (_extendedGaugesWindow != null)
        {
            _extendedGaugesWindow.Activate();
            return;
        }

        var (left, top) = ComputePopupPosition(ExtendedGaugesWindowInitialWidth, ExtendedGaugesWindowInitialHeight);
        _extendedGaugesWindow = new Views.ExtendedGaugesWindow(_telemetryService, _settingsService, left, top);
        _extendedGaugesWindow.Closed += (_, _) => _extendedGaugesWindow = null;
        _extendedGaugesWindow.Show();
    }

    // ------------------------------------------------------------------ ALIGN (hitch alignment)

    /// <summary>Hands the ALIGN beep choices (on/off, volume) from Settings to the ALIGN window. Safe to call repeatedly.</summary>
    private void ApplyAlignBeepSettings()
    {
        var settings = _settingsService.Load();
        _alignViewModel.ConfigureBeeps(settings.AlignBeepsEnabled, settings.AlignBeepVolume);
    }

    /// <summary>Registers or releases the global Ctrl+Alt+. hotkey to match Settings. Safe to call repeatedly.</summary>
    private void ApplyAlignHotkeySetting()
    {
        if (_hwndSource == null)
        {
            return;
        }

        var wanted = _settingsService.Load().AlignHotkeyEnabled;
        if (wanted && !_alignHotkeyRegistered)
        {
            _alignHotkeyRegistered = RegisterHotKey(_hwndSource.Handle, AlignHotkeyId, MOD_CONTROL | MOD_ALT, VK_OEM_PERIOD);
            if (!_alignHotkeyRegistered)
            {
                AppLogger.Log("Warning: could not register Ctrl+Alt+. ALIGN hotkey (already in use by another app?) - the ALIGN tab still works");
            }
        }
        else if (!wanted && _alignHotkeyRegistered)
        {
            UnregisterHotKey(_hwndSource.Handle, AlignHotkeyId);
            _alignHotkeyRegistered = false;
        }
    }

    // Same three-way toggle as the job log: Closed -> Docked (above the card) -> Closed, and if it has been popped out
    // the same tab (or the hotkey) closes the pop-out instead of opening a second copy.
    private void ToggleAlign_Click(object sender, RoutedEventArgs e)
    {
        switch (_alignState)
        {
            case AlignPanelState.Closed:
                SetAlignDockedOpen(true);
                break;
            case AlignPanelState.Docked:
                SetAlignDockedOpen(false);
                break;
            case AlignPanelState.PoppedOut:
                _alignWindow?.Close();
                break;
        }
    }

    /// <summary>How many screen pixels one card layout unit currently is (the Viewbox's scale).</summary>
    private double MeasureCardScale() =>
        CardBorder.ActualWidth > 0
            ? (CardBorder.TransformToAncestor(this).Transform(new Point(CardBorder.ActualWidth, 0)).X
               - CardBorder.TransformToAncestor(this).Transform(new Point(0, 0)).X) / CardBorder.ActualWidth
            : 1.0;

    /// <summary>The usable area of the screen this window is on, in the DIPs Left/Top/Width/Height use.</summary>
    private Rect WorkingAreaDips()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(area.Left * transform.M11, area.Top * transform.M22, area.Width * transform.M11, area.Height * transform.M22);
    }

    // While a toggle's animation is still running the window's live Height/Top are mid-flight, so a quick second toggle
    // (the hotkey makes that easy) works from the geometry the window had before the first one instead.
    private bool _alignAnimating;
    private int _alignAnimVersion;
    private double _alignBaseHeight, _alignBaseTop;

    private void SetAlignDockedOpen(bool open)
    {
        if (open == (_alignState == AlignPanelState.Docked))
        {
            return;
        }

        _alignState = open ? AlignPanelState.Docked : AlignPanelState.Closed;
        _alignViewModel.IsActive = open;

        var duration = TimeSpan.FromMilliseconds(220);
        var version = ++_alignAnimVersion;
        var scale = MeasureCardScale();
        double drawerTarget;

        if (open)
        {
            // Half the card's height (explicit request), as wide as the card; the gap above the card is included.
            _alignDrawerUnits = Math.Max(AlignDrawerMinUnits, Math.Round(CardBorder.ActualHeight * AlignDrawerFraction)) + AlignDrawerGapUnits;
            drawerTarget = _alignDrawerUnits;
            AlignDrawerBorder.Visibility = Visibility.Visible;

            if (!_isFullscreen)
            {
                if (!_alignAnimating)
                {
                    _alignBaseHeight = Height;
                    _alignBaseTop = Top;
                }

                // Grow the window upward by the drawer's on-screen height so the card stays where it was (checked against the
                // real Viewbox at several window sizes: the card's scale is unchanged and it drifts under 2 px). Err a hair
                // large (the extra is invisible padding) rather than small (the Viewbox would shrink everything to fit).
                var growth = _alignDrawerUnits * scale * 1.005;
                var shift = growth;
                var workTop = WorkingAreaDips().Top;
                if (_alignBaseTop - shift < workTop)
                {
                    // No room above on this screen: move up as far as there is, the card shifts down by the rest.
                    shift = Math.Max(0, _alignBaseTop - workTop);
                }

                _alignWindowGrowth = growth;
                _alignTopShift = shift;
                AnimateProperty(this, HeightProperty, _alignBaseHeight + growth, duration);
                AnimateProperty(this, TopProperty, _alignBaseTop - shift, duration);
            }
        }
        else
        {
            drawerTarget = 0;
            if (!_isFullscreen && _alignWindowGrowth > 0)
            {
                var height = _alignAnimating ? _alignBaseHeight : Height - _alignWindowGrowth;
                var top = _alignAnimating ? _alignBaseTop : Top + _alignTopShift;
                AnimateProperty(this, HeightProperty, Math.Max(MinHeight, height), duration);
                AnimateProperty(this, TopProperty, top, duration);
            }

            _alignWindowGrowth = 0;
            _alignTopShift = 0;
        }

        _alignAnimating = true;
        AnimateProperty(AlignDrawerBorder, HeightProperty, drawerTarget, duration, () =>
        {
            if (version != _alignAnimVersion)
            {
                return; // superseded by a later toggle, which owns the end state
            }

            _alignAnimating = false;
            if (_alignState != AlignPanelState.Docked)
            {
                AlignDrawerBorder.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void AlignView_PopOutRequested(object? sender, EventArgs e)
    {
        // Default size: the main window's own size shrunk 50% (explicit request), kept within sane bounds. Measured from
        // the window without the docked drawer's growth, which is about to be given back.
        var mainHeight = Height - _alignWindowGrowth;
        var mainTop = Top + _alignTopShift;
        var width = Math.Clamp(Width * AlignPopoutScale, AlignPopoutMinWidth, 1100);
        var height = Math.Clamp(mainHeight * AlignPopoutScale, AlignPopoutMinHeight, 700);

        // Opens where the docked panel was - just above the main window - or below it when there is no room above;
        // always kept fully on this screen.
        var area = WorkingAreaDips();
        var left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - width));
        var top = mainTop - height - 8;
        if (top < area.Top)
        {
            top = mainTop + mainHeight + 8;
        }

        top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - height));

        SetAlignDockedOpen(false);

        _alignWindow = new Views.AlignWindow(_alignViewModel, left, top, width, height);
        _alignWindow.Closed += (_, _) =>
        {
            _alignWindow = null;
            _alignState = AlignPanelState.Closed;
            _alignViewModel.IsActive = false;
        };
        _alignWindow.Show();

        _alignState = AlignPanelState.PoppedOut;
        _alignViewModel.IsActive = true;
    }

    /// <summary>Animates a double dependency property, then hands it back as a plain value (a held animation would fight anything that sets it directly afterwards).</summary>
    private static void AnimateProperty(DependencyObject target, DependencyProperty property, double to, TimeSpan duration, Action? completed = null)
    {
        var animatable = (IAnimatable)target;
        var from = (double)target.GetValue(property);
        if (double.IsNaN(from) && target is FrameworkElement element)
        {
            from = property == HeightProperty ? element.ActualHeight : element.ActualWidth;
        }

        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed += (_, _) =>
        {
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
            completed?.Invoke();
        };
        animatable.BeginAnimation(property, animation);
    }

    private (double left, double top) ComputePopupPosition(double popupWidth, double popupHeight)
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
        var left = preferredLeft + popupWidth <= screenRight
            ? preferredLeft
            : Left - popupWidth - 12;

        left = Math.Clamp(left, screenLeft, Math.Max(screenLeft, screenRight - popupWidth));
        var top = Math.Clamp(Top, screenTop, Math.Max(screenTop, screenBottom - popupHeight));
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
        ApplyAlignHotkeySetting();
        ApplyAlignBeepSettings();
    }

    private void ModOrder_Click(object sender, RoutedEventArgs e)
    {
        var modOrderWindow = new Views.ModOrderWindow(_settingsService) { Owner = this };
        modOrderWindow.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
