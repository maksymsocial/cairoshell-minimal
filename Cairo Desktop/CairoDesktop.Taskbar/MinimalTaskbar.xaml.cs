using CairoDesktop.AppGrabber;
using CairoDesktop.Application.Interfaces;
using CairoDesktop.Common;
using CairoDesktop.Infrastructure.ObjectModel;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotifyIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// Small flat panel taskbar: start button, task buttons (icon + title), tray, clock, show desktop.
    /// </summary>
    public partial class MinimalTaskbar : CairoAppBarWindow
    {
        private readonly ShellManager _shellManager;
        private readonly IDesktopManager _desktopManager;
        private readonly IAppGrabber _appGrabber;
        private readonly ICommandService _commandService;
        private readonly Settings _settings;

        private ListCollectionView _tasks;
        private ListCollectionView _trayShown;
        private ListCollectionView _trayOverflow;
        private DispatcherTimer _clockTimer;

        private int _openMenus;
        private int _openPopups;
        private readonly List<ApplicationWindow> _minimizedByShowDesktop = new List<ApplicationWindow>();

        public static readonly DependencyProperty ButtonMaxWidthProperty = DependencyProperty.Register(
            nameof(ButtonMaxWidth), typeof(double), typeof(MinimalTaskbar), new PropertyMetadata(200.0));

        public double ButtonMaxWidth
        {
            get => (double)GetValue(ButtonMaxWidthProperty);
            set => SetValue(ButtonMaxWidthProperty, value);
        }

        private bool _disableAutoHide;
        /// <summary>Set by the window service while the mouse is over another bar on the same edge.</summary>
        public bool DisableAutoHide
        {
            get => _disableAutoHide;
            set
            {
                _disableAutoHide = value;
                OnPropertyChanged("AllowAutoHide");
                updateFade();
            }
        }

        public MinimalTaskbar(ICairoApplication cairoApplication,
            ShellManager shellManager,
            IWindowManager windowManager,
            IDesktopManager desktopManager,
            IAppGrabber appGrabber,
            ICommandService commandService,
            Settings settings,
            AppBarScreen screen,
            AppBarEdge edge)
            : base(cairoApplication, shellManager, windowManager, screen, edge, settingsToMode(settings), settings.MinTaskbarHeight)
        {
            // Only make the window see-through when it fades or slides; an opaque window keeps the
            // simplest, crispest text. ClearTypeHint on the opaque root keeps ClearType in the other modes.
            AllowsTransparency = settings.MinTaskbarSlideAway || settings.MinTaskbarFade;

            InitializeComponent();

            _shellManager = shellManager;
            _desktopManager = desktopManager;
            _appGrabber = appGrabber;
            _commandService = commandService;
            _settings = settings;

            ProcessScreenChanges = !Screen.Primary && !_settings.EnableMenuBarMultiMon;

            DesiredHeight = _settings.MinTaskbarHeight;
            AutoHideElement = bdrRoot;
            applyTiming();
            applyLook();
            applyEdge();
            applyParts();

            setupTasks();
            setupTray();
            setupClock();
            setupTimers();
            setupWinNumbers();

            _settings.PropertyChanged += Settings_PropertyChanged;
            _windowManager.ScreensChanged += WindowManager_ScreensChanged;

            MouseEnter += (s, e) => updateFade();
            MouseLeave += (s, e) => updateFade();

            SourceInitialized += (s, e) => useSoftwareRendering();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_Repaint;
            Microsoft.Win32.SystemEvents.PowerModeChanged += SystemEvents_Repaint;
            Microsoft.Win32.SystemEvents.SessionSwitch += SystemEvents_Repaint;
            Closed += (s, e) =>
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_Repaint;
                Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemEvents_Repaint;
                Microsoft.Win32.SystemEvents.SessionSwitch -= SystemEvents_Repaint;
            };
        }

        /// <summary>
        /// WPF's GPU renderer loses the bar's content when the D3D device is reset (full screen games,
        /// sleep, display changes) and then only redraws what changes, e.g. hovered buttons, so the bar
        /// sits blank. A tiny bar renders fine on the CPU, which has no device to lose.
        /// </summary>
        private void useSoftwareRendering()
        {
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source && source.CompositionTarget != null)
            {
                source.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }
        }

        private void SystemEvents_Repaint(object sender, EventArgs e)
        {
            // belt and braces: redraw everything once things have settled
            Dispatcher.BeginInvoke(new Action(repaintAll), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void repaintAll()
        {
            bdrRoot.InvalidateVisual();
            foreach (var child in findVisuals(bdrRoot)) child.InvalidateVisual();
            if (Handle != IntPtr.Zero) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        private static IEnumerable<UIElement> findVisuals(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement element) yield return element;
                foreach (var nested in findVisuals(child)) yield return nested;
            }
        }

        private const uint RDW_INVALIDATE = 0x1, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private static AppBarMode settingsToMode(Settings settings)
        {
            if (settings.MinTaskbarSlideAway) return AppBarMode.AutoHide;
            return settings.MinTaskbarReserveSpace ? AppBarMode.Normal : AppBarMode.None;
        }

        #region Setup
        private void setupTasks()
        {
            if (_shellManager.Tasks.GroupedWindows?.SourceCollection is IList source)
            {
                _tasks = new ListCollectionView(source);
                _tasks.Filter = taskFilter;
                _tasks.IsLiveFiltering = true;
                _tasks.LiveFilteringProperties.Add("ShowInTaskbar");
                _tasks.LiveFilteringProperties.Add("HMonitor");
                lstTasks.ItemsSource = _tasks;
                setupTaskOrder();
            }
        }

        private bool taskFilter(object obj)
        {
            if (!(obj is ApplicationWindow window) || !window.ShowInTaskbar)
            {
                return false;
            }

            if (!_settings.EnableTaskbarMultiMon || _settings.TaskbarMultiMonMode == 0)
            {
                return true;
            }

            if (_settings.TaskbarMultiMonMode == 2)
            {
                return Screen.Primary;
            }

            IntPtr hMonitor = window.HMonitor;
            bool known = _windowManager.ScreenState.Any(s => s.HMonitor == hMonitor);
            if (!known) return Screen.Primary;

            return hMonitor == Screen.HMonitor;
        }

        private void setupTray()
        {
            NotificationArea area = _shellManager.NotificationArea;

            if (_settings.MinTaskbarShowTray && area.Handle == IntPtr.Zero)
            {
                area.Initialize();
            }

            _trayShown = new ListCollectionView(area.TrayIcons) { Filter = o => trayState(o as NotifyIcon) == 0 };
            _trayOverflow = new ListCollectionView(area.TrayIcons) { Filter = o => trayState(o as NotifyIcon) == 1 };

            foreach (var view in new[] { _trayShown, _trayOverflow })
            {
                view.IsLiveFiltering = true;
                view.LiveFilteringProperties.Add("IsHidden");
                view.LiveFilteringProperties.Add("Identifier");
            }

            ((System.Collections.Specialized.INotifyCollectionChanged)_trayOverflow).CollectionChanged += (s, e) => updateOverflowButton();

            lstTray.ItemsSource = _trayShown;
            lstTrayHidden.ItemsSource = _trayOverflow;
            updateOverflowButton();
        }

        /// <summary>0 = shown, 1 = overflow, 2 = not shown</summary>
        private int trayState(NotifyIcon icon)
        {
            if (icon == null || icon.IsHidden) return 2;
            string id = icon.Identifier;
            if (_settings.MinTaskbarRemovedTrayIcons?.Contains(id) == true) return 2;
            if (_settings.MinTaskbarHiddenTrayIcons?.Contains(id) == true) return 1;
            return 0;
        }

        private void updateOverflowButton()
        {
            bool collapsed = _settings.MinTaskbarTrayCollapsed;

            btnTrayOverflow.Visibility = !collapsed && _trayOverflow != null && !_trayOverflow.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            lstTray.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

            // collapse toggle: ">" pushes icons away, "<" brings them back
            btnTrayCollapse.IsChecked = collapsed;
            pathTrayCollapse.Data = Geometry.Parse(collapsed ? "M 3 0 L 0 3 L 3 6" : "M 0 0 L 3 3 L 0 6");
            btnTrayCollapse.ToolTip = collapsed ? "Show tray icons" : "Hide tray icons";
        }

        private void TrayCollapse_Click(object sender, RoutedEventArgs e)
        {
            _settings.MinTaskbarTrayCollapsed = !_settings.MinTaskbarTrayCollapsed;
        }

        private void setupClock()
        {
            _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => updateClock();
            _clockTimer.Start();
            updateClock();
        }

        private void updateClock()
        {
            DateTime now = DateTime.Now;
            string text;
            try
            {
                text = now.ToString(string.IsNullOrWhiteSpace(_settings.MinTaskbarClockFormat) ? "HH:mm" : _settings.MinTaskbarClockFormat);
            }
            catch (FormatException)
            {
                text = now.ToString("HH:mm");
            }

            if (txtClock.Text != text) txtClock.Text = text;
            btnClock.ToolTip = now.ToString("D");
        }
        #endregion

        #region Appearance
        private static Brush brushOr(string hex, Brush fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                {
                    var b = new SolidColorBrush(c);
                    b.Freeze();
                    return b;
                }
            }
            catch (Exception) { /* not a color yet (still typing): keep the old one */ }

            return fallback;
        }

        private void applyLook()
        {
            Resources["MinBg"] = brushOr(_settings.MinTaskbarBackground, Resources["MinBg"] as Brush);
            Resources["MinFg"] = brushOr(_settings.MinTaskbarForeground, Resources["MinFg"] as Brush);
            Resources["MinActiveBg"] = brushOr(_settings.MinTaskbarActiveBackground, Resources["MinActiveBg"] as Brush);
            Resources["MinHoverBg"] = brushOr(_settings.MinTaskbarHoverBackground, Resources["MinHoverBg"] as Brush);
            Resources["MinBorder"] = brushOr(_settings.MinTaskbarBorder, Resources["MinBorder"] as Brush);
            Resources["MinActiveFg"] = brushOr(_settings.MinTaskbarActiveForeground, Resources["MinActiveFg"] as Brush);
            Resources["MinFlashBg"] = brushOr(_settings.MinTaskbarFlashBackground, Resources["MinFlashBg"] as Brush);
            Resources["MinStartBg"] = brushOr(_settings.MinTaskbarStartBackground, Brushes.Transparent);
            Resources["MinStartFg"] = brushOr(_settings.MinTaskbarStartForeground, Resources["MinFg"] as Brush);
            Resources["MinTaskBg"] = brushOr(_settings.MinTaskbarTaskBackground, Brushes.Transparent);
            Resources["MinTaskFg"] = brushOr(_settings.MinTaskbarTaskForeground, Resources["MinFg"] as Brush);
            Resources["MinTaskAltBg"] = brushOr(_settings.MinTaskbarTaskAltBackground, Resources["MinTaskBg"] as Brush);
            Resources["MinTaskAltFg"] = brushOr(_settings.MinTaskbarTaskAltForeground, Resources["MinTaskFg"] as Brush);
            Resources["MinUserBg"] = brushOr(_settings.MinTaskbarUserBackground, Brushes.Transparent);
            Resources["MinUserFg"] = brushOr(_settings.MinTaskbarUserForeground, Resources["MinFg"] as Brush);

            try
            {
                if (!string.IsNullOrWhiteSpace(_settings.MinTaskbarFontFamily))
                    Resources["MinFontFamily"] = new FontFamily(_settings.MinTaskbarFontFamily);
            }
            catch (ArgumentException) { }

            Resources["MinFontSize"] = _settings.MinTaskbarFontSize;
            Resources["MinIconSize"] = (double)_settings.MinTaskbarIconSize;
            Resources["MinTrayCellWidth"] = (double)(_settings.MinTaskbarIconSize + 4);
            ButtonMaxWidth = _settings.MinTaskbarButtonMaxWidth;

            // square start button, full bar height, glyph scaled to the bar
            btnStart.Width = Math.Max(16, _settings.MinTaskbarHeight);
            double glyph = Math.Max(8, Math.Min(12, Math.Round(_settings.MinTaskbarHeight * 0.5)));
            gridStartGlyph.Width = glyph;
            gridStartGlyph.Height = glyph;

            if (!AllowsTransparency)
            {
                // opaque window: paint the window itself so nothing shows black during resizes
                Background = Resources["MinBg"] as Brush;
            }
        }

        private void applyEdge()
        {
            bool top = AppBarEdge == AppBarEdge.Top;
            bdrRoot.BorderThickness = new Thickness(0); // no edge line

            PlacementMode placement = top ? PlacementMode.Bottom : PlacementMode.Top;
            popClock.Placement = placement;
            popTrayOverflow.Placement = placement;
        }

        private void applyParts()
        {
            Visibility vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

            btnStart.Visibility = vis(_settings.MinTaskbarShowStart);
            pnlTray.Visibility = vis(_settings.MinTaskbarShowTray);
            btnClock.Visibility = vis(_settings.MinTaskbarShowClock);
            btnShowDesktop.Visibility = vis(_settings.MinTaskbarShowDesktopButton);
            btnUser.Visibility = vis(_settings.MinTaskbarShowUser);
            txtUser.Text = string.IsNullOrWhiteSpace(_settings.MinTaskbarUserLabel) ? Environment.UserName : _settings.MinTaskbarUserLabel;
        }

        private void applyTiming()
        {
            // used by the base class for slide-away (auto-hide) mode
            AutoHideShowDelayMs = _settings.MinTaskbarShowDelayMs;
            AutoHideDelayMs = _settings.MinTaskbarHideDelayMs;
            AutoHideAnimationMs = Math.Max(1, _settings.MinTaskbarAnimationMs);
            AutoHideShowAnimationMs = Math.Max(1, _settings.MinTaskbarAnimationMs);
        }

        private bool isFadeMode => _settings.MinTaskbarFade && !_settings.MinTaskbarSlideAway;

        private bool isHot => IsMouseOver || _openMenus > 0 || _openPopups > 0 || DisableAutoHide;

        #region Fade
        private DispatcherTimer _fadeTimer;
        private double _alpha = 1.0;
        private double _fadeFrom = 1.0;
        private double _fadeTo = 1.0;
        private DateTime _fadeStart;
        private double _fadeDurationMs;

        private void setAlpha(double alpha)
        {
            _alpha = Math.Max(0, Math.Min(1, alpha));

            // never fully 0: fully transparent pixels of a layered window let clicks fall through
            bdrRoot.Opacity = Math.Max(0.02, _alpha);
        }

        private void clearAlpha()
        {
            _fadeTimer?.Stop();
            _alpha = 1.0;
            bdrRoot.Opacity = 1.0;
        }

        /// <summary>Fade mode: fully opaque while in use, idle opacity otherwise.</summary>
        private void updateFade(bool immediate = false)
        {
            if (!isFadeMode)
            {
                clearAlpha();
                return;
            }

            bool hot = isHot;
            double target = hot ? 1.0 : _settings.MinTaskbarIdleOpacity;
            int delay = immediate ? 0 : (hot ? _settings.MinTaskbarShowDelayMs : _settings.MinTaskbarHideDelayMs);

            _fadeFrom = _alpha;
            _fadeTo = target;
            _fadeStart = DateTime.Now.AddMilliseconds(delay);
            _fadeDurationMs = immediate ? 0 : Math.Max(0, _settings.MinTaskbarAnimationMs);

            if (_fadeTimer == null)
            {
                _fadeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                _fadeTimer.Tick += FadeTimer_Tick;
            }

            _fadeTimer.Start();
            FadeTimer_Tick(null, null);
        }

        private void FadeTimer_Tick(object sender, EventArgs e)
        {
            double elapsed = (DateTime.Now - _fadeStart).TotalMilliseconds;
            if (elapsed < 0) return; // still in the delay

            double t = _fadeDurationMs <= 0 ? 1 : Math.Min(1, elapsed / _fadeDurationMs);
            double eased = 0.5 - 0.5 * Math.Cos(Math.PI * t);
            setAlpha(_fadeFrom + (_fadeTo - _fadeFrom) * eased);

            if (t >= 1) _fadeTimer.Stop();
        }
        #endregion

        protected override bool ShouldAllowAutoHide()
        {
            return !DisableAutoHide && _openPopups == 0 && _openMenus == 0 && base.ShouldAllowAutoHide();
        }
        #endregion

        #region Window lifecycle
        protected override void OnSourceInitialized(object sender, EventArgs e)
        {
            base.OnSourceInitialized(sender, e);

            Left = Screen.Bounds.Left / DpiScale;
            Width = Screen.Bounds.Width / DpiScale;

            if (AppBarMode != AppBarMode.Normal && Screen.Primary)
            {
                _desktopManager.ResetPosition(false);
            }

            updateFade(true);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            NativeMethods.SetWindowLong(Handle, NativeMethods.WindowLongFlags.GWL_EXSTYLE,
                NativeMethods.GetWindowLong(Handle, NativeMethods.WindowLongFlags.GWL_EXSTYLE) | (int)NativeMethods.ExtendedWindowStyles.WS_EX_NOACTIVATE);

            updateFade(true);
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)NativeMethods.WM.MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            }

            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        protected override void CustomClosing()
        {
            if (_windowManager?.IsSettingDisplays == true || AllowClose)
            {
                _clockTimer?.Stop();
                _timerTick?.Stop();
                teardownWinNumbers();
                _settings.PropertyChanged -= Settings_PropertyChanged;
                _windowManager.ScreensChanged -= WindowManager_ScreensChanged;
                lstTasks.ItemsSource = null;
                lstTray.ItemsSource = null;
                lstTrayHidden.ItemsSource = null;
                if (_tasks != null) _tasks.Filter = null;
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e?.PropertyName)
            {
                case "MinTaskbarHeight":
                    DesiredHeight = _settings.MinTaskbarHeight;
                    applyLook();
                    UpdatePosition();
                    if (EnvironmentHelper.IsAppRunningAsShell) _appBarManager.SetWorkArea(Screen);
                    break;
                case "MinTaskbarBackground":
                    applyLook();
                    _trayShown?.Refresh();
                    _trayOverflow?.Refresh();
                    break;
                case "MinTaskbarForeground":
                case "MinTaskbarActiveBackground":
                case "MinTaskbarHoverBackground":
                case "MinTaskbarBorder":
                case "MinTaskbarActiveForeground":
                case "MinTaskbarFlashBackground":
                case "MinTaskbarStartBackground":
                case "MinTaskbarStartForeground":
                case "MinTaskbarTaskBackground":
                case "MinTaskbarTaskForeground":
                case "MinTaskbarTaskAltBackground":
                case "MinTaskbarTaskAltForeground":
                case "MinTaskbarUserBackground":
                case "MinTaskbarUserForeground":
                case "MinTaskbarFontFamily":
                case "MinTaskbarFontSize":
                case "MinTaskbarButtonMaxWidth":
                case "MinTaskbarIconSize":
                    applyLook();
                    break;
                case "MinTaskbarShowStart":
                case "MinTaskbarShowTray":
                case "MinTaskbarShowClock":
                case "MinTaskbarShowDesktopButton":
                case "MinTaskbarShowUser":
                case "MinTaskbarUserLabel":
                    applyParts();
                    if (_settings.MinTaskbarShowTray && _shellManager.NotificationArea.Handle == IntPtr.Zero)
                        _shellManager.NotificationArea.Initialize();
                    break;
                case "MinTaskbarWinNumbers":
                    if (Screen.Primary) WinNumberHook.Enabled = _settings.MinTaskbarWinNumbers;
                    break;
                case "MinTaskbarClockFormat":
                    updateClock();
                    break;
                case "MinTaskbarTrayCollapsed":
                    updateOverflowButton();
                    break;
                case "MinTaskbarHiddenTrayIcons":
                case "MinTaskbarRemovedTrayIcons":
                    _trayShown?.Refresh();
                    _trayOverflow?.Refresh();
                    updateOverflowButton();
                    break;
                case "MinTaskbarShowDelayMs":
                case "MinTaskbarHideDelayMs":
                case "MinTaskbarAnimationMs":
                    applyTiming();
                    break;
                case "MinTaskbarIdleOpacity":
                    updateFade(true);
                    break;
                case "EnableTaskbarMultiMon":
                case "TaskbarMultiMonMode":
                    _tasks?.Refresh();
                    break;
            }
        }

        private void WindowManager_ScreensChanged(object sender, WindowManagerEventArgs e)
        {
            if (e.DisplaysChanged) _tasks?.Refresh();
        }

        private void takeFocus()
        {
            // we are WS_EX_NOACTIVATE, so popups/menus would not close on outside click without this
            NativeMethods.SetForegroundWindow(Handle);
        }

        private NativeMenu.NativeRect barRect()
        {
            NativeMethods.GetWindowRect(Handle, out NativeMethods.Rect r);
            return new NativeMenu.NativeRect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        }

        /// <summary>Runs a native menu while keeping the bar shown (no fade / slide while it is open).</summary>
        private void withMenuOpen(Action show)
        {
            _openMenus++;
            updateFade(true);
            try
            {
                show();
            }
            finally
            {
                _openMenus = Math.Max(0, _openMenus - 1);
                OnPropertyChanged("AllowAutoHide");
                updateFade();
            }
        }

        private void showMenu(NativeMenu menu)
        {
            if (menu.Count == 0) return;
            // open after the mouse event finishes so WPF is not holding input while the menu loop runs
            Dispatcher.BeginInvoke(new Action(() => withMenuOpen(() => menu.Show(Handle, barRect(), AppBarEdge == AppBarEdge.Top))));
        }

        private void Panel_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            var menu = new NativeMenu()
                .Add("Taskbar settings...", () => MinimalTaskbarSettings.ShowWindow(_settings, _shellManager))
                .Add("Tray icons...", () => MinimalTaskbarSettings.ShowWindow(_settings, _shellManager, true))
                .Add("Show Cairo menu bar", () => _settings.EnableMenuBar = !_settings.EnableMenuBar, true, _settings.EnableMenuBar)
                .Separator()
                .Add("Run...", () => _commandService.InvokeCommand("ShowRunDialog"))
                .Add("Task Manager", () => _commandService.InvokeCommand("StartTaskManager"))
                .Add("Cairo settings...", () => _commandService.InvokeCommand("OpenCairoSettings"));

            showMenu(menu);
        }

        private void AnyPopup_Opened(object sender, EventArgs e)
        {
            _openPopups++;
            takeFocus();
            if (sender == popClock) onClockPopupOpened();
            updateFade();
        }

        private void AnyPopup_Closed(object sender, EventArgs e)
        {
            _openPopups = Math.Max(0, _openPopups - 1);
            if (sender == popClock) updateTimerTick();
            OnPropertyChanged("AllowAutoHide");
            updateFade();
        }

        #endregion

        #region Task buttons
        private void TaskButton_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressNextClick) { _suppressNextClick = false; return; }
            if (!((sender as FrameworkElement)?.DataContext is ApplicationWindow window)) return;

            _minimizedByShowDesktop.Clear();

            if (window.State == ApplicationWindow.WindowState.Active && !window.IsMinimized && window.CanMinimize)
            {
                window.Minimize();
            }
            else
            {
                window.BringToFront();
            }
        }

        private void TaskButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (!((sender as FrameworkElement)?.DataContext is ApplicationWindow window)) return;

            e.Handled = true;

            if (_settings.TaskbarMiddleClick == 1)
            {
                window.Close();
                return;
            }

            // new instance of the same program
            try
            {
                if (!window.IsUWP && !string.IsNullOrEmpty(window.WinFileName))
                {
                    Process.Start(new ProcessStartInfo(window.WinFileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Warning($"MinimalTaskbar: unable to start new instance: {ex.Message}");
            }
        }

        private void TaskButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is ApplicationWindow window)) return;

            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => showWindowSystemMenu(window)));
        }

        [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
        [DllImport("user32.dll")] private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);
        [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);

        private const uint SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030,
                           SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;
        private const uint MF_BYCOMMAND = 0x0, MF_ENABLED = 0x0, MF_GRAYED = 0x1;

        /// <summary>
        /// Task button menu: Open file location and Close.
        /// </summary>
        private void showWindowSystemMenu(ApplicationWindow window)
        {
            IntPtr hwnd = window.Handle;

            string exe = window.IsUWP ? null : window.WinFileName;
            bool hasExe = !string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe);

            var menu = new NativeMenu()
                .Add("Open file location", () => openFileLocation(exe), hasExe)
                .Separator()
                .Add("Close", () => NativeMethods.PostMessage(hwnd, (uint)NativeMethods.WM.SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero));

            showMenu(menu);
        }

        private static void openFileLocation(string exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Warning($"MinimalTaskbar: unable to open file location: {ex.Message}");
            }
        }
        #endregion

        #region Start
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            switch (_settings.MinTaskbarStartAction)
            {
                case 1:
                    _commandService.InvokeCommand("ToggleProgramsMenu");
                    break;
                case 2:
                    const byte VK_LWIN = 0x5B;
                    const uint KEYEVENTF_KEYUP = 0x2;
                    keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    break;
                default:
                    showMenu(buildStartMenu());
                    break;
            }
        }

        private NativeMenu buildStartMenu()
        {
            var menu = new NativeMenu();

            var categories = _appGrabber.CategoryList?
                .Where(c => c.Count > 0 && (c.Type == AppCategoryType.Standard || c.Type == AppCategoryType.Uncategorized))
                .ToList() ?? new List<Category>();

            foreach (Category category in categories)
            {
                NativeMenu sub = menu.Submenu(category.DisplayName);
                foreach (ApplicationInfo app in category.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    ApplicationInfo captured = app;
                    sub.Add(app.Name, () => _appGrabber.LaunchProgram(captured), true, false, app.Icon);
                }
            }

            menu.Separator()
                .Add("Run...", () => _commandService.InvokeCommand("ShowRunDialog"))
                .Add("Task Manager", () => _commandService.InvokeCommand("StartTaskManager"))
                .Add("Taskbar settings...", () => MinimalTaskbarSettings.ShowWindow(_settings, _shellManager))
                .Add("Cairo settings...", () => _commandService.InvokeCommand("OpenCairoSettings"))
                .Separator()
                .Add("Lock", () => _commandService.InvokeCommand("Lock"))
                .Add("Sleep", () => _commandService.InvokeCommand("Sleep"))
                .Add("Restart...", () => _commandService.InvokeCommand("Restart"))
                .Add("Shut down...", () => _commandService.InvokeCommand("ShutDown"))
                .Add("Log off...", () => _commandService.InvokeCommand("LogOff"));

            return menu;
        }
        #endregion

        #region User / power
        [DllImport("user32.dll")] private static extern bool LockWorkStation();
        [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private void User_Click(object sender, RoutedEventArgs e)
        {
            var menu = new NativeMenu()
                .Add(Environment.UserDomainName + "\\" + Environment.UserName, null, false)
                .Separator()
                .Add("Lock", () => LockWorkStation())
                .Add("Sign out", () => power("Sign out", "/l"))
                .Separator()
                .Add("Sleep", () => powerAction("Sleep", () => SetSuspendState(false, false, false)))
                .Add("Hibernate", () => power("Hibernate", "/h"))
                .Add("Restart", () => power("Restart", "/r /t 0"))
                .Add("Shut down", () => power("Shut down", "/s /t 0"));

            showMenu(menu);
        }

        private void power(string what, string shutdownArgs)
        {
            powerAction(what, () => runShutdown(shutdownArgs));
        }

        private void powerAction(string what, Action action)
        {
            if (_settings.MinTaskbarConfirmPower)
            {
                var answer = MessageBox.Show($"{what} now?", what, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (answer != MessageBoxResult.OK) return;
            }

            action();
        }

        private static void runShutdown(string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Warning($"MinimalTaskbar: shutdown {args} failed: {ex.Message}");
            }
        }
        #endregion

        #region Show desktop
        private void ShowDesktop_Click(object sender, RoutedEventArgs e)
        {
            var windows = (_shellManager.Tasks.GroupedWindows?.SourceCollection as IEnumerable)?
                .OfType<ApplicationWindow>()
                .Where(w => w.ShowInTaskbar)
                .ToList() ?? new List<ApplicationWindow>();

            var open = windows.Where(w => !w.IsMinimized && w.CanMinimize).ToList();

            if (open.Count > 0)
            {
                // minimize everything, remember what we did
                _minimizedByShowDesktop.Clear();
                _minimizedByShowDesktop.AddRange(open);
                foreach (var w in open) w.Minimize();
            }
            else if (_minimizedByShowDesktop.Count > 0)
            {
                // second click brings them back, oldest first so the last active ends up on top
                foreach (var w in _minimizedByShowDesktop.Where(windows.Contains).Reverse())
                {
                    w.Restore();
                }
                _minimizedByShowDesktop.Clear();
            }
        }
        #endregion

        #region Clock
        private void Clock_Click(object sender, RoutedEventArgs e)
        {
            // popup is bound to IsChecked
        }
        #endregion

        #region Tray
        private void setTrayHostSizeData()
        {
            double dpi = DpiScale;
            _shellManager.NotificationArea.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = (NativeMethods.ABEdge)AppBarEdge,
                rc = new NativeMethods.Rect
                {
                    Top = (int)(Top * dpi),
                    Left = (int)(Left * dpi),
                    Bottom = (int)((Top + ActualHeight) * dpi),
                    Right = (int)((Left + ActualWidth) * dpi)
                }
            });
        }

        private void TrayIcon_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is NotifyIcon icon)) return;
            e.Handled = true;

            // Ctrl + right click: customize this icon instead of passing the click to the app
            if (e.ChangedButton == MouseButton.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                return;
            }

            setTrayHostSizeData();
            icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), System.Windows.Forms.SystemInformation.DoubleClickTime);
        }

        private void TrayIcon_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is NotifyIcon icon)) return;
            e.Handled = true;

            if (e.ChangedButton == MouseButton.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                showTrayIconMenu(icon, fe);
                return;
            }

            icon.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), System.Windows.Forms.SystemInformation.DoubleClickTime);
        }

        private void TrayIcon_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is NotifyIcon icon)) return;

            // position for Shell_NotifyIconGetRect
            Point location = fe.PointToScreen(new Point(0, 0));
            double dpi = DpiScale;
            icon.Placement = new NativeMethods.Rect
            {
                Top = (int)location.Y,
                Left = (int)location.X,
                Bottom = (int)(fe.ActualHeight * dpi),
                Right = (int)(fe.ActualWidth * dpi)
            };
            icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());
        }

        private static readonly string[] _systemIconGuids =
        {
            NotificationArea.HARDWARE_GUID, NotificationArea.UPDATE_GUID, NotificationArea.MICROPHONE_GUID,
            NotificationArea.LOCATION_GUID, NotificationArea.MEETNOW_GUID, NotificationArea.NETWORK_GUID,
            NotificationArea.POWER_GUID, NotificationArea.VOLUME_GUID
        };

        private bool isLightBackground =>
            (Resources["MinBg"] as SolidColorBrush)?.Color is Color c && (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 140;

        private void TrayIconImage_Loaded(object sender, RoutedEventArgs e)
        {
            // Windows' own tray icons (network, volume, ...) are white: invert them on a light panel
            if (!(sender is Image image) || !(image.DataContext is NotifyIcon icon)) return;

            bool system = EnvironmentHelper.IsWindows10OrBetter && _systemIconGuids.Contains(icon.GUID.ToString(), StringComparer.OrdinalIgnoreCase);
            image.Effect = system && isLightBackground ? new InvertEffect() : null;
        }

        private void TrayIcon_MouseLeave(object sender, MouseEventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as NotifyIcon)?.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        }

        private void TrayIcon_MouseMove(object sender, MouseEventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as NotifyIcon)?.IconMouseMove(MouseHelper.GetCursorPositionParam());
        }

        private void showTrayIconMenu(NotifyIcon icon, FrameworkElement target)
        {
            string id = icon.Identifier;
            int state = trayState(icon);

            string title = string.IsNullOrEmpty(icon.Title) ? id : icon.Title.Split('\n')[0];

            var menu = new NativeMenu()
                .Add(title, null, false)
                .Separator()
                .Add("Show in tray", () => MinimalTaskbarSettings.SetTrayIconState(_settings, id, 0), true, state == 0)
                .Add("Move to hidden icons", () => MinimalTaskbarSettings.SetTrayIconState(_settings, id, 1), true, state == 1)
                .Add("Don't show", () => MinimalTaskbarSettings.SetTrayIconState(_settings, id, 2))
                .Separator()
                .Add("Tray icons...", () => MinimalTaskbarSettings.ShowWindow(_settings, _shellManager, true));

            showMenu(menu);
        }
        #endregion

        #region Panel context menu
        private void OpenMinSettings_Click(object sender, RoutedEventArgs e) => MinimalTaskbarSettings.ShowWindow(_settings, _shellManager);

        private void OpenTraySettings_Click(object sender, RoutedEventArgs e) => MinimalTaskbarSettings.ShowWindow(_settings, _shellManager, true);

        private void Run_Click(object sender, RoutedEventArgs e) => _commandService.InvokeCommand("ShowRunDialog");

        private void TaskManager_Click(object sender, RoutedEventArgs e) => _commandService.InvokeCommand("StartTaskManager");

        private void CairoSettings_Click(object sender, RoutedEventArgs e) => _commandService.InvokeCommand("OpenCairoSettings");
        #endregion
    }
}
