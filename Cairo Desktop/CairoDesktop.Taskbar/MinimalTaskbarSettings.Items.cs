using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CairoDesktop.Taskbar
{
    /// <summary>Items tab: the bar's items in order, like xfce's panel item list.</summary>
    public partial class MinimalTaskbarSettings
    {
        private sealed class ItemRow
        {
            public string Spec;
            public string Text;
            public override string ToString() => Text;
        }

        private string[] items() =>
            _settings.MinTaskbarItems == null || _settings.MinTaskbarItems.Length == 0 ? MinimalTaskbar.DefaultItems : _settings.MinTaskbarItems;

        private static string describe(string spec)
        {
            MinimalTaskbar.SplitItem(spec, out string kind, out string arg);
            switch (kind)
            {
                case "start": return "Start button";
                case "tasks": return "Window buttons  (fills the free room)";
                case "tray": return "Tray icons";
                case "user": return "User name / power menu";
                case "clock": return "Clock, timers and stopwatch";
                case "desktop": return "Show desktop";
                case "separator": return "──  Separator";
                case "spacer": return "↔  Spacer  (fills the free room)";
                case "space": return "Space, " + (string.IsNullOrEmpty(arg) ? "8" : arg) + " px";
                case "launcher": return "Launcher:  " + MinimalTaskbar.launcherName(Environment.ExpandEnvironmentVariables(arg.Trim('"'))) + "   (" + arg + ")";
                case "script":
                    string path = MinScriptRunner.ResolvePath(arg);
                    string name = File.Exists(path) ? MinScriptHeader.Read(path).Name : null;
                    return "Script:  " + (string.IsNullOrWhiteSpace(name) ? arg : name + "   (" + arg + ")") + (File.Exists(path) ? "" : "   - file not found");
                default: return spec + "   (unknown)";
            }
        }

        private void refreshItems()
        {
            int sel = lstItems.SelectedIndex;
            lstItems.ItemsSource = items().Select(s => new ItemRow { Spec = s, Text = describe(s) }).ToList();
            if (sel >= 0) lstItems.SelectedIndex = Math.Min(sel, lstItems.Items.Count - 1);
        }

        private void setItems(List<string> list, int select)
        {
            _settings.MinTaskbarItems = list.ToArray(); // refreshItems runs from PropertyChanged
            lstItems.SelectedIndex = Math.Max(-1, Math.Min(select, list.Count - 1));
            lstItems.Focus();
        }

        /// <summary>New items go after the selected one, or before the tray (where xfce users expect plugins).</summary>
        private void insertItem(string spec)
        {
            var list = items().ToList();
            int at = lstItems.SelectedIndex >= 0 ? lstItems.SelectedIndex + 1 : list.IndexOf("tray");
            if (at < 0 || at > list.Count) at = list.Count;
            list.Insert(at, spec);
            setItems(list, at);
        }

        /// <summary>Native light Win32 menu, like the bar's own menus (a WPF ContextMenu would get Cairo's theme).</summary>
        private void ItemAdd_Click(object sender, RoutedEventArgs e)
        {
            var menu = new NativeMenu()
                .Add("Separator", () => insertItem("separator"))
                .Add("Spacer (fills the free room)", () => insertItem("spacer"))
                .Add("Space (8 px)", () => insertItem("space:8"))
                .Separator()
                .Add("Launcher (app, file, folder)...", addLauncher);

            var scripts = menu.Submenu("Script");
            string[] files = MinScriptExamples.Scripts();
            foreach (string f in files)
            {
                string file = f;
                scripts.Add(file, () => insertItem("script:" + file));
            }
            if (files.Length > 0) scripts.Separator();
            scripts.Add("Other file...", addScriptFile);

            menu.Submenu("New script")
                .Add("PowerShell (.ps1)", () => newScript(".ps1"))
                .Add("Nushell (.nu)", () => newScript(".nu"))
                .Add("Python (.py)", () => newScript(".py"))
                .Add("Batch (.bat)", () => newScript(".bat"));

            string[] have = items().Select(s => { MinimalTaskbar.SplitItem(s, out string k, out _); return k; }).ToArray();
            var missing = MinimalTaskbar.BuiltInItems.Where(k => !have.Contains(k)).ToList();
            if (missing.Count > 0)
            {
                menu.Separator();
                foreach (string k in missing)
                {
                    string kind = k;
                    menu.Add(describe(kind), () => insertItem(kind));
                }
            }

            // drop down under the button, opened after the click finishes
            var button = sender as FrameworkElement;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                IntPtr owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Point tl = button.PointToScreen(new Point(0, 0));
                Point br = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                var rect = new NativeMenu.NativeRect { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y };
                menu.Show(owner, rect, true);
            }));
        }

        private void addLauncher()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Launcher: pick an app, shortcut or file",
                Filter = "Apps and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.url|All files|*.*",
                DereferenceLinks = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + "\\Programs"
            };
            if (dlg.ShowDialog(this) == true) insertItem("launcher:" + dlg.FileName);
        }

        private void addScriptFile()
        {
            MinScriptExamples.EnsureFolder();
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Script for the bar",
                Filter = "Scripts|*.ps1;*.nu;*.py;*.js;*.bat;*.cmd;*.exe|All files|*.*",
                InitialDirectory = MinScriptExamples.Folder
            };
            if (dlg.ShowDialog(this) != true) return;

            // keep it short when it lives in the scripts folder
            string file = dlg.FileName;
            if (string.Equals(Path.GetDirectoryName(file), MinScriptExamples.Folder, StringComparison.OrdinalIgnoreCase))
                file = Path.GetFileName(file);
            insertItem("script:" + file);
        }

        private void newScript(string extension)
        {
            string name = askName();
            if (name == null) return;
            string file = MinScriptExamples.CreateNew(name, extension);
            insertItem("script:" + file);
            MinScriptExamples.Edit(Path.Combine(MinScriptExamples.Folder, file));
        }

        private string askName()
        {
            var box = new TextBox { Text = "my-item", Margin = new Thickness(0, 6, 0, 10), VerticalContentAlignment = VerticalAlignment.Center, Height = 24 };
            var ok = new Button { Content = "Create", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Name of the new script:" });
            panel.Children.Add(box);
            panel.Children.Add(buttons);

            var dlg = new Window
            {
                Title = "New script",
                Content = panel,
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 300,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            ok.Click += (s, e) => dlg.DialogResult = true;
            dlg.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return dlg.ShowDialog() == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
        }

        private void ItemRemove_Click(object sender, RoutedEventArgs e)
        {
            int i = lstItems.SelectedIndex;
            if (i < 0) return;
            var list = items().ToList();
            list.RemoveAt(i);
            setItems(list, i);
        }

        private void moveSelected(int by)
        {
            int i = lstItems.SelectedIndex;
            var list = items().ToList();
            int to = i + by;
            if (i < 0 || to < 0 || to >= list.Count) return;
            string it = list[i];
            list.RemoveAt(i);
            list.Insert(to, it);
            setItems(list, to);
        }

        private void ItemUp_Click(object sender, RoutedEventArgs e) => moveSelected(-1);
        private void ItemDown_Click(object sender, RoutedEventArgs e) => moveSelected(1);

        private void Items_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { ItemRemove_Click(null, null); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Up) { moveSelected(-1); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Down) { moveSelected(1); e.Handled = true; }
        }

        private void ItemEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!(lstItems.SelectedItem is ItemRow row)) return;
            MinimalTaskbar.SplitItem(row.Spec, out string kind, out string arg);
            if (kind == "script") MinScriptExamples.Edit(MinScriptRunner.ResolvePath(arg));
            else if (kind == "space")
            {
                // cycle a few handy widths
                int[] widths = { 4, 8, 16, 32 };
                int.TryParse(arg, out int w);
                int next = widths.FirstOrDefault(x => x > w);
                var list = items().ToList();
                list[lstItems.SelectedIndex] = "space:" + (next == 0 ? 4 : next);
                setItems(list, lstItems.SelectedIndex);
            }
        }

        private void Items_DoubleClick(object sender, MouseButtonEventArgs e) => ItemEdit_Click(null, null);

        private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string kind = "";
            if (lstItems.SelectedItem is ItemRow row) MinimalTaskbar.SplitItem(row.Spec, out kind, out _);
            btnItemEdit.IsEnabled = kind == "script" || kind == "space";
            btnItemEdit.Content = kind == "space" ? "Wider" : "Edit script";
        }

        private void ItemsDefault_Click(object sender, RoutedEventArgs e) => setItems(MinimalTaskbar.DefaultItems.ToList(), -1);

        private void OpenScripts_Click(object sender, RoutedEventArgs e) => MinScriptExamples.OpenFolder();

        private void RestoreExamples_Click(object sender, RoutedEventArgs e)
        {
            MinScriptExamples.RestoreExamples();
            refreshItems();
        }
    }
}
