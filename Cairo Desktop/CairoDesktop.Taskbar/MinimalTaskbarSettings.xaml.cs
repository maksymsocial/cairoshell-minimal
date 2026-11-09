using CairoDesktop.Common;
using ManagedShell;
using ManagedShell.AppBar;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using NotifyIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace CairoDesktop.Taskbar
{
    public partial class MinimalTaskbarSettings : Window
    {
        private static MinimalTaskbarSettings _instance;

        private readonly Settings _settings;
        private readonly ShellManager _shellManager;

        /// <summary>Opens the window on a tab: "items" or "tray".</summary>
        public static void ShowWindow(Settings settings, ShellManager shellManager, string tab)
        {
            ShowWindow(settings, shellManager, tab == "tray");
            if (tab == "items") _instance.tabs.SelectedItem = _instance.tabItems;
        }

        public static void ShowWindow(Settings settings, ShellManager shellManager, bool trayTab = false)
        {
            if (_instance == null)
            {
                _instance = new MinimalTaskbarSettings(settings, shellManager);
                _instance.Closed += (s, e) => _instance = null;
                _instance.Show();
            }

            if (trayTab) _instance.tabs.SelectedItem = _instance.tabTray;
            _instance.refreshTrayList();
            _instance.Activate();
        }

        /// <summary>state: 0 = show, 1 = hidden icons (overflow), 2 = don't show</summary>
        public static void SetTrayIconState(Settings settings, string identifier, int state)
        {
            if (string.IsNullOrEmpty(identifier)) return;

            var hidden = (settings.MinTaskbarHiddenTrayIcons ?? new string[0]).Where(i => i != identifier).ToList();
            var removed = (settings.MinTaskbarRemovedTrayIcons ?? new string[0]).Where(i => i != identifier).ToList();

            if (state == 1) hidden.Add(identifier);
            if (state == 2) removed.Add(identifier);

            settings.MinTaskbarHiddenTrayIcons = hidden.ToArray();
            settings.MinTaskbarRemovedTrayIcons = removed.ToArray();
        }

        private MinimalTaskbarSettings(Settings settings, ShellManager shellManager)
        {
            InitializeComponent();

            _settings = settings;
            _shellManager = shellManager;
            DataContext = _settings;

            syncRadios();
            refreshThemes();
            refreshItems();

            MinAlarm.PlayingChanged += MinAlarm_PlayingChanged;
            Closed += (s, e) =>
            {
                MinAlarm.PlayingChanged -= MinAlarm_PlayingChanged;
                if (_testingAlarm) MinAlarm.Stop();
            };
            _settings.PropertyChanged += Settings_PropertyChanged;
            Closed += (s, e) => _settings.PropertyChanged -= Settings_PropertyChanged;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e?.PropertyName)
            {
                case "TaskbarStyle":
                case "TaskbarEdge":
                case "MinTaskbarSlideAway":
                    syncRadios();
                    break;
                case "MinTaskbarItems":
                    refreshItems();
                    break;
            }
        }

        private void syncRadios()
        {
            radStyleMinimal.IsChecked = _settings.TaskbarStyle == 1;
            radStyleCairo.IsChecked = _settings.TaskbarStyle != 1;
            radEdgeTop.IsChecked = _settings.TaskbarEdge == AppBarEdge.Top;
            radEdgeBottom.IsChecked = _settings.TaskbarEdge != AppBarEdge.Top;

            // sliding away already gets the bar out of the way, so reserve/fade don't apply then
            chkSlide.IsChecked = _settings.MinTaskbarSlideAway;
            chkReserve.IsEnabled = !_settings.MinTaskbarSlideAway;
            chkFade.IsEnabled = !_settings.MinTaskbarSlideAway;

            radPaletteLight.IsChecked = _settings.MinTaskbarPaletteMode != 1;
            radPaletteDark.IsChecked = _settings.MinTaskbarPaletteMode == 1;
        }

        private void refreshTrayList()
        {
            lstTrayIcons.ItemsSource = _shellManager.NotificationArea.TrayIcons
                .Where(i => !i.IsHidden)
                .GroupBy(i => i.Identifier)
                .Select(g => new TrayIconRow(_settings, g.First()))
                .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private void Style_Click(object sender, RoutedEventArgs e)
        {
            _settings.TaskbarStyle = radStyleMinimal.IsChecked == true ? 1 : 0;
        }

        private void Edge_Click(object sender, RoutedEventArgs e)
        {
            _settings.TaskbarEdge = radEdgeTop.IsChecked == true ? AppBarEdge.Top : AppBarEdge.Bottom;
        }

        private void Slide_Click(object sender, RoutedEventArgs e)
        {
            bool value = chkSlide.IsChecked == true;

            if (value && _settings.EnableMenuBarAutoHide && _settings.TaskbarEdge == _settings.MenuBarEdge)
            {
                // two auto-hide bars cannot share one screen edge
                _settings.EnableMenuBarAutoHide = false;
            }

            _settings.MinTaskbarSlideAway = value;
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _settings.MinTaskbarHeight = 18;
            _settings.MinTaskbarIconSize = 14;
            _settings.MinTaskbarFade = false;
            _settings.MinTaskbarReserveSpace = true;
            _settings.MinTaskbarSlideAway = false;
            _settings.MinTaskbarButtonMaxWidth = 200;
            _settings.MinTaskbarFontSize = 12;
            _settings.MinTaskbarFontFamily = "Segoe UI";
            _settings.MinTaskbarBackground = "#D6D6D6";
            _settings.MinTaskbarForeground = "#000000";
            _settings.MinTaskbarActiveBackground = "#F4F4F4";
            _settings.MinTaskbarHoverBackground = "#E4E4E4";
            _settings.MinTaskbarBorder = "#9A9A9A";
            _settings.MinTaskbarActiveForeground = "#000000";
            _settings.MinTaskbarFlashBackground = "#F2C46B";
            _settings.MinTaskbarStartBackground = "";
            _settings.MinTaskbarStartForeground = "#000000";
            _settings.MinTaskbarTaskBackground = "";
            _settings.MinTaskbarTaskForeground = "";
            _settings.MinTaskbarTaskAltBackground = "";
            _settings.MinTaskbarTaskAltForeground = "";
            _settings.MinTaskbarUserBackground = "";
            _settings.MinTaskbarUserForeground = "";
            _settings.MinTaskbarIdleOpacity = 0.35;
            _settings.MinTaskbarShowDelayMs = 0;
            _settings.MinTaskbarHideDelayMs = 600;
            _settings.MinTaskbarAnimationMs = 150;
            _settings.MinTaskbarShowStart = true;
            _settings.MinTaskbarStartAction = 2;
            _settings.MinTaskbarShowTray = true;
            _settings.MinTaskbarShowClock = true;
            _settings.MinTaskbarClockFormat = "HH:mm";
            _settings.MinTaskbarShowDesktopButton = true;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        #region Timer alarm
        private bool _testingAlarm;

        private void AlarmBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Timer alarm sound",
                Filter = MinAlarm.FileFilter,
                CheckFileExists = true
            };
            string current = MinAlarm.Resolve(_settings.MinTaskbarAlarmSound);
            if (current != null) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(current);

            if (dlg.ShowDialog(this) == true) _settings.MinTaskbarAlarmSound = dlg.FileName;
        }

        private void AlarmDefault_Click(object sender, RoutedEventArgs e) => _settings.MinTaskbarAlarmSound = "";

        private void AlarmTest_Click(object sender, RoutedEventArgs e)
        {
            if (MinAlarm.IsPlaying)
            {
                MinAlarm.Stop();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_settings.MinTaskbarAlarmSound)
                && !System.IO.File.Exists(Environment.ExpandEnvironmentVariables(_settings.MinTaskbarAlarmSound.Trim().Trim('"'))))
            {
                MessageBox.Show(this, "That file doesn't exist, so the Windows reminder sound will play instead.", "Timer alarm",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            _testingAlarm = true;
            MinAlarm.Play(_settings.MinTaskbarAlarmSound, _settings.MinTaskbarAlarmVolume, TimeSpan.Zero);
        }

        private void AlarmVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // change the volume of a playing test (or alarm) live
            MinAlarm.SetVolume((int)Math.Round(e.NewValue));
        }

        private void MinAlarm_PlayingChanged(object sender, EventArgs e)
        {
            btnAlarmTest.Content = MinAlarm.IsPlaying ? "Stop" : "Test";
            if (!MinAlarm.IsPlaying) _testingAlarm = false;
        }
        #endregion

        #region Colors
        private string getColorSetting(string name) =>
            typeof(Settings).GetProperty(name)?.GetValue(_settings) as string;

        private void setColorSetting(string name, string value) =>
            typeof(Settings).GetProperty(name)?.SetValue(_settings, value);

        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string name)) return;

            using (var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true })
            {
                if (MinimalPalette.TryParse(getColorSetting(name), out Color current) && current.A > 0)
                {
                    dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var c = dialog.Color;
                    setColorSetting(name, MinimalPalette.ToHex(Color.FromRgb(c.R, c.G, c.B)));
                }
            }
        }

        private void ClearColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string name) setColorSetting(name, "");
        }

        private void PaletteMode_Click(object sender, RoutedEventArgs e)
        {
            _settings.MinTaskbarPaletteMode = radPaletteDark.IsChecked == true ? 1 : 0;
        }

        private void ApplyPalette_Click(object sender, RoutedEventArgs e)
        {
            if (!MinimalPalette.TryParse(_settings.MinTaskbarPaletteAccent, out Color accent))
            {
                MessageBox.Show(this, "The accent isn't a color yet. Use #RRGGBB, e.g. #3D7BD9.", "Palette", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var pair in MinimalPalette.Generate(accent, _settings.MinTaskbarPaletteMode == 1))
            {
                setColorSetting(pair.Key, pair.Value);
            }
        }

        private void WindowsAccent_Click(object sender, RoutedEventArgs e)
        {
            // the accent color from Windows Settings > Personalization > Colors (stored as ABGR)
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (key?.GetValue("AccentColor") is int abgr)
                    {
                        byte r = (byte)(abgr & 0xFF), g = (byte)((abgr >> 8) & 0xFF), b = (byte)((abgr >> 16) & 0xFF);
                        _settings.MinTaskbarPaletteAccent = MinimalPalette.ToHex(Color.FromRgb(r, g, b));
                        return;
                    }
                }
            }
            catch (Exception) { }

            Color glass = SystemParameters.WindowGlassColor;
            _settings.MinTaskbarPaletteAccent = MinimalPalette.ToHex(glass);
        }

        private static readonly Random _random = new Random();

        private void RandomAccent_Click(object sender, RoutedEventArgs e)
        {
            // random hue, pleasant saturation/lightness
            double h = _random.Next(0, 360);
            Color c = hslToColor(h, 0.55 + _random.NextDouble() * 0.3, 0.45 + _random.NextDouble() * 0.1);
            _settings.MinTaskbarPaletteAccent = MinimalPalette.ToHex(c);
            ApplyPalette_Click(sender, e);
        }

        private static Color hslToColor(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
        #endregion

        #region Themes
        private void refreshThemes(string select = null)
        {
            lstThemes.ItemsSource = MinimalThemes.List();
            if (select != null) lstThemes.SelectedItem = select;
        }

        private string selectedTheme => lstThemes.SelectedItem as string;

        private void Themes_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // so saving again overwrites the theme you picked
            if (selectedTheme != null) txtThemeName.Text = selectedTheme;
        }

        private void ThemeName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) SaveTheme_Click(sender, e);
        }

        private void SaveTheme_Click(object sender, RoutedEventArgs e)
        {
            string name = MinimalThemes.CleanName(txtThemeName.Text);
            if (name == null)
            {
                MessageBox.Show(this, "Type a name for the theme first.", "Themes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MinimalThemes.Exists(name) &&
                MessageBox.Show(this, $"Replace the theme \"{name}\"?", "Themes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                MinimalThemes.Save(_settings, name);
                refreshThemes(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't save the theme: {ex.Message}", "Themes", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadTheme_Click(object sender, RoutedEventArgs e)
        {
            string name = selectedTheme;
            if (name == null) return;

            try
            {
                MinimalThemes.Load(_settings, name);
                syncRadios();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't load the theme: {ex.Message}", "Themes", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Themes_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadTheme_Click(sender, e);

        private void DeleteTheme_Click(object sender, RoutedEventArgs e)
        {
            string name = selectedTheme;
            if (name == null) return;

            if (MessageBox.Show(this, $"Delete the theme \"{name}\"?", "Themes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                MinimalThemes.Delete(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't delete the theme: {ex.Message}", "Themes", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            refreshThemes();
        }

        private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(MinimalThemes.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{MinimalThemes.Folder}\"") { UseShellExecute = true });
            }
            catch (Exception) { }
        }
        #endregion

        private class TrayIconRow : INotifyPropertyChanged
        {
            private readonly Settings _settings;

            public TrayIconRow(Settings settings, NotifyIcon icon)
            {
                _settings = settings;
                Icon = icon.Icon;
                Identifier = icon.Identifier;
                Title = string.IsNullOrWhiteSpace(icon.Title) ? (icon.Path ?? icon.Identifier) : icon.Title.Split('\n')[0];
            }

            public ImageSource Icon { get; }
            public string Identifier { get; }
            public string Title { get; }

            public int State
            {
                get
                {
                    if (_settings.MinTaskbarRemovedTrayIcons?.Contains(Identifier) == true) return 2;
                    if (_settings.MinTaskbarHiddenTrayIcons?.Contains(Identifier) == true) return 1;
                    return 0;
                }
                set
                {
                    SetTrayIconState(_settings, Identifier, value);
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
