using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// The bar's items, left to right, from MinTaskbarItems (like the items of an xfce panel).
    /// </summary>
    public partial class MinimalTaskbar
    {
        public static readonly string[] DefaultItems = { "start", "tasks", "tray", "user", "clock", "desktop" };

        public static readonly string[] BuiltInItems = { "start", "tasks", "tray", "user", "clock", "desktop" };

        private readonly List<FrameworkElement> _customItems = new List<FrameworkElement>();
        private readonly List<Action> _itemCleanup = new List<Action>();
        private readonly HashSet<string> _placedBuiltIns = new HashSet<string>();

        public static void SplitItem(string spec, out string kind, out string arg)
        {
            spec = (spec ?? "").Trim();
            int i = spec.IndexOf(':');
            // "C:\..." is not a kind: kinds are plain words
            if (i > 0 && spec.Substring(0, i).All(char.IsLetter))
            {
                kind = spec.Substring(0, i).ToLowerInvariant();
                arg = spec.Substring(i + 1).Trim();
            }
            else
            {
                kind = spec.ToLowerInvariant();
                arg = "";
            }
        }

        private string[] currentItems() =>
            _settings.MinTaskbarItems == null || _settings.MinTaskbarItems.Length == 0 ? DefaultItems : _settings.MinTaskbarItems;

        private FrameworkElement builtIn(string kind)
        {
            switch (kind)
            {
                case "start": return btnStart;
                case "tasks": return lstTasks;
                case "tray": return pnlTray;
                case "user": return btnUser;
                case "clock": return btnClock;
                case "desktop": return btnShowDesktop;
                default: return null;
            }
        }

        private void applyItems()
        {
            foreach (var c in _itemCleanup) c();
            _itemCleanup.Clear();
            foreach (var el in _customItems) gridItems.Children.Remove(el);
            _customItems.Clear();
            _placedBuiltIns.Clear();
            gridItems.ColumnDefinitions.Clear();

            string[] items = currentItems();
            bool hasFill = false;
            int col = 0;

            for (int index = 0; index < items.Length; index++)
            {
                SplitItem(items[index], out string kind, out string arg);
                FrameworkElement el = builtIn(kind);
                bool fill = false;
                bool custom = false;

                if (el != null)
                {
                    if (!_placedBuiltIns.Add(kind)) continue; // each built-in once
                    fill = kind == "tasks";
                }
                else
                {
                    custom = true;
                    switch (kind)
                    {
                        case "separator": el = makeSeparator(); break;
                        case "spacer": el = new Border { Background = Brushes.Transparent }; fill = true; break;
                        case "space":
                            el = new Border { Background = Brushes.Transparent, Width = double.TryParse(arg, out double w) ? Math.Max(0, w) : 8 };
                            break;
                        case "launcher": el = makeLauncher(arg); break;
                        case "script": el = makeScript(arg); break;
                    }
                    if (el == null) continue;
                }

                gridItems.ColumnDefinitions.Add(new ColumnDefinition { Width = fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                Grid.SetColumn(el, col++);
                hasFill |= fill;

                if (custom)
                {
                    int at = index;
                    el.Tag = at;
                    el.MouseRightButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        showItemMenu(at, s as FrameworkElement);
                    };
                    gridItems.Children.Add(el);
                    _customItems.Add(el);
                }
            }

            // nothing stretches: items pack to the left, like an xfce panel without an expander
            if (!hasFill) gridItems.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            foreach (string kind in BuiltInItems.Where(k => !_placedBuiltIns.Contains(k)))
            {
                Grid.SetColumn(builtIn(kind), 0);
            }
        }

        private bool inItems(string kind) => _placedBuiltIns.Contains(kind);

        #region Simple items
        private FrameworkElement makeSeparator()
        {
            var line = new Border { Width = 1, Margin = new Thickness(3, 3, 3, 3) };
            line.SetResourceReference(Border.BackgroundProperty, "MinBorder");
            return new Border { Background = Brushes.Transparent, Child = line };
        }

        private FrameworkElement makeLauncher(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            string path = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));

            var img = new Image { VerticalAlignment = VerticalAlignment.Center };
            img.SetResourceReference(WidthProperty, "MinIconSize");
            img.SetResourceReference(HeightProperty, "MinIconSize");
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            try
            {
                img.Source = IconImageConverter.GetImageFromAssociatedIcon(path, IconSize.Small);
            }
            catch (Exception) { }

            var btn = new Button
            {
                Content = img,
                Padding = new Thickness(5, 0, 5, 0),
                ToolTip = launcherName(path)
            };
            btn.SetResourceReference(StyleProperty, "MinFlatButton");
            btn.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ""
                    })?.Dispose();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Couldn't open " + path + "\n\n" + ex.Message, "Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            return btn;
        }

        public static string launcherName(string path)
        {
            try
            {
                if (path.Contains("://")) return path;
                string name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch (Exception) { return path; }
        }
        #endregion

        #region Script items
        private FrameworkElement makeScript(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            MinScriptExamples.EnsureFolder();

            var runner = MinScriptRunner.Acquire(spec);

            var icon = new Image { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed };
            icon.SetResourceReference(WidthProperty, "MinIconSize");
            icon.SetResourceReference(HeightProperty, "MinIconSize");
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            // vertical meter for <bar>
            var barFill = new Border { VerticalAlignment = VerticalAlignment.Bottom };
            barFill.SetResourceReference(Border.BackgroundProperty, "MinFg");
            var barBox = new Border
            {
                Width = 5,
                Margin = new Thickness(0, 3, 4, 3),
                BorderThickness = new Thickness(1),
                Child = barFill,
                Visibility = Visibility.Collapsed
            };
            barBox.SetResourceReference(Border.BorderBrushProperty, "MinFg");

            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(icon);
            panel.Children.Add(barBox);
            panel.Children.Add(text);

            var btn = new Button { Content = panel, Padding = new Thickness(6, 0, 6, 0) };
            btn.SetResourceReference(StyleProperty, "MinFlatButton");
            ToolTipService.SetInitialShowDelay(btn, 400);

            string lastIcon = null;
            void update()
            {
                MinScriptState st = runner.State;
                btn.Visibility = st.Hidden ? Visibility.Collapsed : Visibility.Visible;
                text.Text = st.Text ?? "";
                text.Visibility = string.IsNullOrEmpty(st.Text) ? Visibility.Collapsed : Visibility.Visible;
                btn.ToolTip = string.IsNullOrWhiteSpace(st.Tooltip) ? null : st.Tooltip;
                btn.MinWidth = st.Width ?? 0;

                Brush fg = colorOrNull(st.Foreground);
                if (fg != null) text.Foreground = fg; else text.ClearValue(TextBlock.ForegroundProperty);
                if (st.IsError) text.Opacity = 0.7; else text.ClearValue(OpacityProperty);

                Brush bg = colorOrNull(st.Background);
                if (bg != null) btn.Background = bg; else btn.ClearValue(BackgroundProperty);

                if (st.Bar is double pct)
                {
                    barBox.Visibility = Visibility.Visible;
                    double inner = Math.Max(0, (btn.ActualHeight > 0 ? btn.ActualHeight : _settings.MinTaskbarHeight) - 8);
                    barFill.Height = inner * pct / 100.0;
                }
                else barBox.Visibility = Visibility.Collapsed;

                if (st.Icon != lastIcon)
                {
                    lastIcon = st.Icon;
                    icon.Source = loadIcon(st.Icon, runner.Path);
                    icon.Visibility = icon.Source == null ? Visibility.Collapsed : Visibility.Visible;
                }
            }

            EventHandler onUpdated = (s, e) => update();
            runner.Updated += onUpdated;
            btn.SizeChanged += (s, e) => { if (e.HeightChanged) update(); };
            update();

            _itemCleanup.Add(() =>
            {
                runner.Updated -= onUpdated;
                runner.Release();
            });

            btn.Click += (s, e) =>
            {
                MinScriptState st = runner.State;
                if (st.IsError) { runner.Restart(); return; }
                if (!string.IsNullOrWhiteSpace(st.Click))
                {
                    MinScriptRunner.RunCommand(st.Click, Path.GetDirectoryName(runner.Path));
                    refreshSoon(runner);
                }
                else runner.SendEvent("click left");
            };
            btn.MouseUp += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                e.Handled = true;
                MinScriptState st = runner.State;
                if (!string.IsNullOrWhiteSpace(st.MiddleClick))
                {
                    MinScriptRunner.RunCommand(st.MiddleClick, Path.GetDirectoryName(runner.Path));
                    refreshSoon(runner);
                }
                else runner.SendEvent("click middle");
            };
            btn.MouseWheel += (s, e) =>
            {
                e.Handled = true;
                runner.SendEvent(e.Delta > 0 ? "scroll up" : "scroll down");
            };

            btn.DataContext = runner;
            return btn;
        }

        private void refreshSoon(MinScriptRunner runner)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (s, e) => { t.Stop(); runner.Refresh(); };
            t.Start();
        }

        private static Brush colorOrNull(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try
            {
                if (ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                {
                    var b = new SolidColorBrush(c);
                    b.Freeze();
                    return b;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>An image file, or any file/exe whose icon to show.</summary>
        private static ImageSource loadIcon(string icon, string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(icon)) return null;
            try
            {
                string p = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
                if (!Path.IsPathRooted(p)) p = Path.Combine(Path.GetDirectoryName(scriptPath) ?? "", p);
                if (!File.Exists(p)) return null;

                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".ico" || ext == ".tif" || ext == ".tiff")
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(p);
                    bmp.DecodePixelHeight = 32;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                return IconImageConverter.GetImageFromAssociatedIcon(p, IconSize.Small);
            }
            catch (Exception) { return null; }
        }
        #endregion

        #region Item menu
        private void showItemMenu(int index, FrameworkElement el)
        {
            string[] items = currentItems();
            if (index < 0 || index >= items.Length) return;
            SplitItem(items[index], out string kind, out string arg);

            var menu = new NativeMenu();

            if (kind == "script" && el?.DataContext is MinScriptRunner runner)
            {
                foreach (var entry in runner.State.Menu)
                {
                    var e = entry;
                    if (e.Key == "-" || string.IsNullOrEmpty(e.Key)) { menu.Separator(); continue; }
                    menu.Add(e.Key, () =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Value))
                        {
                            MinScriptRunner.RunCommand(e.Value, Path.GetDirectoryName(runner.Path));
                            refreshSoon(runner);
                        }
                        else runner.SendEvent("menu " + e.Key);
                    });
                }
                menu.Separator()
                    .Add("Refresh", () => runner.Refresh())
                    .Add("Edit script", () => MinScriptExamples.Edit(runner.Path), File.Exists(runner.Path))
                    .Add("Open scripts folder", () => MinScriptExamples.OpenFolder());
            }
            else if (kind == "launcher")
            {
                string path = Environment.ExpandEnvironmentVariables(arg.Trim('"'));
                menu.Add("Open file location", () => openFileLocation(path), File.Exists(path) || Directory.Exists(path));
            }

            menu.Separator()
                .Add("Move left", () => moveItem(index, -1), index > 0)
                .Add("Move right", () => moveItem(index, 1), index < items.Length - 1)
                .Add("Remove", () => removeItem(index))
                .Separator()
                .Add("Edit items...", () => MinimalTaskbarSettings.ShowWindow(_settings, _shellManager, "items"));

            showMenu(menu);
        }

        private void moveItem(int index, int by)
        {
            var list = currentItems().ToList();
            int to = index + by;
            if (index < 0 || to < 0 || index >= list.Count || to >= list.Count) return;
            string it = list[index];
            list.RemoveAt(index);
            list.Insert(to, it);
            _settings.MinTaskbarItems = list.ToArray();
        }

        private void removeItem(int index)
        {
            var list = currentItems().ToList();
            if (index < 0 || index >= list.Count) return;
            list.RemoveAt(index);
            _settings.MinTaskbarItems = list.ToArray();
        }
        #endregion
    }
}
