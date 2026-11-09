using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CairoDesktop.Taskbar
{
    /// <summary>One countdown in the clock popup.</summary>
    public class MinTimer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; set; }
        public TimeSpan Total { get; set; }

        /// <summary>When it ends (while running).</summary>
        public DateTime EndsAtUtc { get; set; }

        /// <summary>Time left while paused.</summary>
        public TimeSpan PausedLeft { get; set; }

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; set { _isRunning = value; raise(nameof(IsRunning)); raise(nameof(ToggleTip)); } }

        private bool _isDone;
        public bool IsDone { get => _isDone; set { _isDone = value; raise(nameof(IsDone)); raise(nameof(ToggleTip)); } }

        /// <summary>Done and not yet seen: keeps the alarm going.</summary>
        public bool Ringing { get; set; }

        public string ToggleTip => IsDone ? "Restart" : IsRunning ? "Pause" : "Resume";

        public TimeSpan Left => IsDone ? TimeSpan.Zero : IsRunning ? EndsAtUtc - DateTime.UtcNow : PausedLeft;

        private string _text = "";
        public string Text => _text;

        public void Refresh()
        {
            string t = IsDone ? "done" : MinimalTaskbar.FormatLeft(Left);
            if (t != _text) { _text = t; raise(nameof(Text)); }

            double p = Total.TotalMilliseconds <= 0 ? 0 : Left.TotalMilliseconds / Total.TotalMilliseconds;
            p = Math.Max(0, Math.Min(1, p));
            if (Math.Abs(p - _progress) > 0.0005 || (p == 0) != (_progress == 0)) { _progress = p; raise(nameof(Progress)); }
        }

        private double _progress = 1;
        public double Progress => _progress;

        private void raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A ring that empties as Progress goes 1 -> 0: starts at the top and shrinks back counter-clockwise.</summary>
    public class MinRing : FrameworkElement
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(MinRing), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(MinRing), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(MinRing), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            double p = Math.Max(0, Math.Min(1, Progress));
            double t = Thickness;
            double r = (Math.Min(ActualWidth, ActualHeight) - t) / 2;
            if (p <= 0.001 || r <= 0) return;

            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            var pen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };

            if (p >= 0.999)
            {
                dc.DrawEllipse(null, pen, c, r, r);
                return;
            }

            double start = -Math.PI / 2;               // 12 o'clock
            double end = start - p * 2 * Math.PI;      // going counter-clockwise
            var a = new Point(c.X + r * Math.Cos(start), c.Y + r * Math.Sin(start));
            var b = new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end));

            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(a, false, false);
                ctx.ArcTo(b, new Size(r, r), 0, p > 0.5, SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    public class MinLap
    {
        public string Name { get; set; }
        public string Split { get; set; }
        public string Total { get; set; }
    }

    public partial class MinimalTaskbar
    {
        private readonly ObservableCollection<MinTimer> _timers = new ObservableCollection<MinTimer>();
        private readonly ObservableCollection<MinLap> _laps = new ObservableCollection<MinLap>();
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private TimeSpan _lastLap = TimeSpan.Zero;
        private DispatcherTimer _timerTick;
        private DateTime _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        private bool _blinkOn;

        #region Setup
        private void setupTimers()
        {
            lstTimers.ItemsSource = _timers;
            lstLaps.ItemsSource = _laps;
            _timers.CollectionChanged += (s, e) => txtTimersEmpty.Visibility = _timers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var fmt = CultureInfo.CurrentCulture.DateTimeFormat;
            for (int i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)fmt.FirstDayOfWeek + i) % 7);
                string name = fmt.GetAbbreviatedDayName(day);
                gridCalHead.Children.Add(new TextBlock
                {
                    Text = name.Length > 2 ? name.Substring(0, 2) : name,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            _timerTick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
            _timerTick.Tick += (s, e) => timerTick();
        }

        /// <summary>Runs the tick only while something needs it.</summary>
        private void updateTimerTick()
        {
            bool needed = popClock.IsOpen || _stopwatch.IsRunning || _timers.Any(t => t.IsRunning || t.Ringing);
            if (needed && !_timerTick.IsEnabled) _timerTick.Start();
            else if (!needed && _timerTick.IsEnabled) _timerTick.Stop();
            updateBarTimer();
        }
        #endregion

        #region Tick
        private void timerTick()
        {
            foreach (var t in _timers)
            {
                if (t.IsRunning && t.EndsAtUtc <= DateTime.UtcNow)
                {
                    t.IsRunning = false;
                    t.IsDone = true;
                    t.Ringing = true;
                    startRinging();
                }
                t.Refresh();
            }

            if (_timers.Any(t => t.Ringing) && popClock.IsOpen && tabTimers.IsChecked == true) acknowledgeTimers();

            if (popClock.IsOpen && tabStopwatch.IsChecked == true) txtStopwatch.Text = FormatElapsed(_stopwatch.Elapsed);

            updateBarTimer();
            updateTimerTick();
        }

        /// <summary>Countdown / stopwatch shown on the bar next to the clock.</summary>
        private void updateBarTimer()
        {
            string text = null;
            double? ring = null;
            bool ringing = _timers.Any(t => t.Ringing);

            if (ringing)
            {
                var r = _timers.First(t => t.Ringing);
                text = string.IsNullOrEmpty(r.Name) || r.Name == FormatLeft(r.Total) ? "timer done" : r.Name + " done";
            }
            else
            {
                var next = _timers.Where(t => t.IsRunning).OrderBy(t => t.EndsAtUtc).FirstOrDefault();
                if (next != null)
                {
                    text = FormatLeft(next.Left);
                    next.Refresh();
                    ring = next.Progress;
                }
                else if (_stopwatch.IsRunning) text = FormatElapsed(_stopwatch.Elapsed, false);
            }

            ringClockTimer.Visibility = ring == null ? Visibility.Collapsed : Visibility.Visible;
            if (ring != null) ringClockTimer.Progress = ring.Value;

            txtClockTimer.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (text != null && txtClockTimer.Text != text) txtClockTimer.Text = text;

            // blink the clock while an alarm is ringing
            if (ringing)
            {
                _blinkOn = DateTime.UtcNow.Millisecond < 500;
                btnClock.Background = _blinkOn ? Resources["MinFlashBg"] as Brush : Brushes.Transparent;
            }
            else if (btnClock.Background != Brushes.Transparent)
            {
                btnClock.Background = Brushes.Transparent;
            }
        }
        #endregion

        #region Alarm
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_ALIAS = 0x00010000;

        private bool _alarmOn;

        private void startRinging()
        {
            // sound repeats for about 30 s, the clock keeps blinking until seen
            if (_alarmOn && MinAlarm.IsPlaying) return;
            _alarmOn = true;
            MinAlarm.Play(_settings.MinTaskbarAlarmSound, _settings.MinTaskbarAlarmVolume, TimeSpan.FromSeconds(30));
        }

        private void acknowledgeTimers()
        {
            foreach (var t in _timers) t.Ringing = false;
            if (_alarmOn)
            {
                _alarmOn = false;
                MinAlarm.Stop();
            }
            updateBarTimer();
        }
        #endregion

        #region Formatting / parsing
        internal static string FormatLeft(TimeSpan t)
        {
            // round up so a fresh 5 minute timer shows 5:00, and 0:00 only at the end
            long secs = Math.Max(0, (long)Math.Ceiling(t.TotalSeconds - 0.05));
            long h = secs / 3600, m = secs / 60 % 60, s = secs % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        }

        private static string FormatElapsed(TimeSpan t, bool tenths = true)
        {
            long h = (long)t.TotalHours;
            string main = h > 0 ? $"{h}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
            return tenths ? main + "." + (t.Milliseconds / 100) : main;
        }

        /// <summary>
        /// "5" = 5 min, "90s", "1h20m", "1:30" (m:s), "1:00:00" (h:m:s), anything after the time is the name.
        /// </summary>
        private static bool tryParseTimer(string input, out TimeSpan span, out string name)
        {
            span = TimeSpan.Zero;
            name = null;
            input = (input ?? "").Trim();
            if (input.Length == 0) return false;

            var clock = Regex.Match(input, @"^(\d+):(\d{1,2})(?::(\d{1,2}))?(?:\s+(.*))?$");
            if (clock.Success)
            {
                int a = int.Parse(clock.Groups[1].Value), b = int.Parse(clock.Groups[2].Value);
                span = clock.Groups[3].Success
                    ? new TimeSpan(a, b, int.Parse(clock.Groups[3].Value))
                    : TimeSpan.FromSeconds(a * 60 + b);
                name = clock.Groups[4].Value.Trim();
            }
            else
            {
                var units = Regex.Match(input, @"^((?:\d+(?:[.,]\d+)?\s*(?:h|hr|hours?|m|min|mins|minutes?|s|sec|secs|seconds?)\s*)+)(.*)$", RegexOptions.IgnoreCase);
                if (units.Success)
                {
                    foreach (Match p in Regex.Matches(units.Groups[1].Value, @"(\d+(?:[.,]\d+)?)\s*([hms])", RegexOptions.IgnoreCase))
                    {
                        double v = double.Parse(p.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                        switch (char.ToLowerInvariant(p.Groups[2].Value[0]))
                        {
                            case 'h': span += TimeSpan.FromHours(v); break;
                            case 'm': span += TimeSpan.FromMinutes(v); break;
                            default: span += TimeSpan.FromSeconds(v); break;
                        }
                    }
                    name = units.Groups[2].Value.Trim();
                }
                else
                {
                    var plain = Regex.Match(input, @"^(\d+(?:[.,]\d+)?)(?:\s+(.*))?$");
                    if (!plain.Success) return false;
                    span = TimeSpan.FromMinutes(double.Parse(plain.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
                    name = plain.Groups[2].Value.Trim();
                }
            }

            return span.TotalSeconds >= 1 && span.TotalDays < 7;
        }
        #endregion

        #region Popup
        private void onClockPopupOpened()
        {
            txtClockDate.Text = DateTime.Now.ToString("D");
            _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            renderCalendar();

            // a ringing alarm: open straight on the timers
            if (_timers.Any(t => t.Ringing)) tabTimers.IsChecked = true;
            else if (_stopwatch.IsRunning && !_timers.Any(t => t.IsRunning)) tabStopwatch.IsChecked = true;
            else if (_timers.Any(t => t.IsRunning)) tabTimers.IsChecked = true;

            refreshStopwatchUi();
            foreach (var t in _timers) t.Refresh();
            updateTimerTick();
            if (tabTimers.IsChecked == true) focusTimerInput();
        }

        private void ClockTab_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlDate == null) return; // still loading
            pnlDate.Visibility = tabDate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            pnlTimers.Visibility = tabTimers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            pnlStopwatch.Visibility = tabStopwatch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (tabTimers.IsChecked == true)
            {
                acknowledgeTimers();
                if (popClock.IsOpen) focusTimerInput();
            }
            if (tabStopwatch.IsChecked == true) refreshStopwatchUi();
        }

        private void ClockPopup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                btnClock.IsChecked = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Space && tabStopwatch.IsChecked == true)
            {
                StopwatchStart_Click(null, null);
                e.Handled = true;
            }
        }

        /// <summary>The bar never activates, so give the popup window real keyboard focus for typing.</summary>
        private void focusTimerInput()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!popClock.IsOpen) return;
                if (PresentationSource.FromVisual(popClock.Child) is HwndSource src)
                    ManagedShell.Interop.NativeMethods.SetForegroundWindow(src.Handle);
                txtTimerInput.Focus();
                Keyboard.Focus(txtTimerInput);
            }));
        }

        private void TimerInput_PreviewMouseDown(object sender, MouseButtonEventArgs e) => focusTimerInput();
        #endregion

        #region Calendar
        private void renderCalendar()
        {
            txtCalMonth.Text = _calMonth.ToString("MMMM yyyy");
            gridCalDays.Children.Clear();

            var fmt = CultureInfo.CurrentCulture.DateTimeFormat;
            int offset = ((int)_calMonth.DayOfWeek - (int)fmt.FirstDayOfWeek + 7) % 7;
            int days = DateTime.DaysInMonth(_calMonth.Year, _calMonth.Month);
            int cells = (int)Math.Ceiling((offset + days) / 7.0) * 7;
            DateTime start = _calMonth.AddDays(-offset);

            for (int i = 0; i < cells; i++)
            {
                DateTime d = start.AddDays(i);
                bool inMonth = d.Month == _calMonth.Month;
                bool today = d == DateTime.Today;

                var cell = new Border
                {
                    Height = 20,
                    Child = new TextBlock
                    {
                        Text = d.Day.ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                        Opacity = inMonth ? 1 : 0.4
                    }
                };
                if (today) cell.SetResourceReference(Border.BackgroundProperty, "MinActiveBg");
                gridCalDays.Children.Add(cell);
            }
        }

        private void CalPrev_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); renderCalendar(); }
        private void CalNext_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); renderCalendar(); }

        private void CalToday_Click(object sender, RoutedEventArgs e)
        {
            _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            renderCalendar();
        }

        private void Calendar_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _calMonth = _calMonth.AddMonths(e.Delta > 0 ? -1 : 1);
            renderCalendar();
            e.Handled = true;
        }
        #endregion

        #region Timers
        private void addTimer(TimeSpan span, string name)
        {
            var t = new MinTimer
            {
                Name = string.IsNullOrWhiteSpace(name) ? FormatLeft(span) : name,
                Total = span,
                EndsAtUtc = DateTime.UtcNow + span,
                IsRunning = true
            };
            t.Refresh();
            _timers.Add(t);
            updateTimerTick();
        }

        private void TimerAdd_Click(object sender, RoutedEventArgs e)
        {
            if (tryParseTimer(txtTimerInput.Text, out TimeSpan span, out string name))
            {
                addTimer(span, name);
                txtTimerInput.Clear();
            }
            else
            {
                txtTimerInput.SelectAll();
                playAlarmError();
            }
            focusTimerInput();
        }

        private static void playAlarmError() => PlaySound("SystemHand", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);

        private void TimerInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TimerAdd_Click(sender, null);
                e.Handled = true;
            }
        }

        private void TimerInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            txtTimerHint.Visibility = string.IsNullOrEmpty(txtTimerInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TimerQuick_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && int.TryParse(fe.Tag as string, out int minutes))
            {
                // a name typed in the box goes with the quick button
                string name = txtTimerInput.Text.Trim();
                addTimer(TimeSpan.FromMinutes(minutes), name);
                txtTimerInput.Clear();
            }
            focusTimerInput();
        }

        private void TimerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is MinTimer t)) return;

            if (t.IsDone)
            {
                t.IsDone = false;
                t.Ringing = false;
                t.EndsAtUtc = DateTime.UtcNow + t.Total;
                t.IsRunning = true;
            }
            else if (t.IsRunning)
            {
                t.PausedLeft = t.Left;
                t.IsRunning = false;
            }
            else
            {
                t.EndsAtUtc = DateTime.UtcNow + t.PausedLeft;
                t.IsRunning = true;
            }

            t.Refresh();
            updateTimerTick();
            focusTimerInput();
        }

        private void TimerRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MinTimer t)
            {
                _timers.Remove(t);
                if (!_timers.Any(x => x.Ringing)) acknowledgeTimers();
                updateTimerTick();
            }
            focusTimerInput();
        }
        #endregion

        #region Stopwatch
        private void refreshStopwatchUi()
        {
            txtStopwatch.Text = FormatElapsed(_stopwatch.Elapsed);
            btnStopwatchStart.Content = _stopwatch.IsRunning ? "Stop" : _stopwatch.Elapsed > TimeSpan.Zero ? "Resume" : "Start";
            btnStopwatchLap.IsEnabled = _stopwatch.IsRunning;
        }

        private void StopwatchStart_Click(object sender, RoutedEventArgs e)
        {
            if (_stopwatch.IsRunning) _stopwatch.Stop();
            else _stopwatch.Start();
            refreshStopwatchUi();
            updateTimerTick();
        }

        private void StopwatchLap_Click(object sender, RoutedEventArgs e)
        {
            if (!_stopwatch.IsRunning) return;
            TimeSpan now = _stopwatch.Elapsed;
            _laps.Insert(0, new MinLap
            {
                Name = "Lap " + (_laps.Count + 1),
                Split = "+" + FormatElapsed(now - _lastLap),
                Total = FormatElapsed(now)
            });
            _lastLap = now;
        }

        private void StopwatchReset_Click(object sender, RoutedEventArgs e)
        {
            _stopwatch.Reset();
            _laps.Clear();
            _lastLap = TimeSpan.Zero;
            refreshStopwatchUi();
            updateTimerTick();
        }
        #endregion
    }
}
