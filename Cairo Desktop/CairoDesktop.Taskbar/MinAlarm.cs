using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// Plays the timer alarm (wav, mp3, wma, m4a, aac, flac: whatever Windows Media Foundation can open)
    /// at a set volume, optionally looping for a while. Must be used from the UI thread.
    /// </summary>
    internal static class MinAlarm
    {
        public const string FileFilter = "Sound files|*.wav;*.mp3;*.wma;*.m4a;*.aac;*.flac;*.mp4|All files|*.*";

        private static MediaPlayer _player;
        private static DispatcherTimer _gap;
        private static DateTime _loopUntilUtc;
        private static bool _playing;

        public static event EventHandler PlayingChanged;

        public static bool IsPlaying => _playing;

        /// <summary>The Windows "Notification.Reminder" sound file, or another stock one.</summary>
        public static string DefaultSound()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\Notification.Reminder\.Current"))
                {
                    if (key?.GetValue(null) is string s && s.Length > 0)
                    {
                        s = Environment.ExpandEnvironmentVariables(s);
                        if (!Path.IsPathRooted(s)) s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", s);
                        if (File.Exists(s)) return s;
                    }
                }
            }
            catch (Exception) { }

            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            foreach (string f in new[] { "Windows Notify Calendar.wav", "Alarm01.wav", "Windows Notify.wav", "chimes.wav" })
            {
                string p = Path.Combine(media, f);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string Resolve(string custom)
        {
            if (!string.IsNullOrWhiteSpace(custom))
            {
                string p = Environment.ExpandEnvironmentVariables(custom.Trim().Trim('"'));
                if (File.Exists(p)) return p;
            }
            return DefaultSound();
        }

        /// <summary>0..100 slider to player volume; squared so the low end of the slider is usable.</summary>
        private static double toVolume(int percent)
        {
            double v = Math.Max(0, Math.Min(100, percent)) / 100.0;
            return v * v;
        }

        /// <summary>Plays the sound; repeats it (with a short pause) until <paramref name="loopFor"/> has passed.</summary>
        public static void Play(string customFile, int volumePercent, TimeSpan loopFor)
        {
            Stop();
            string file = Resolve(customFile);
            _loopUntilUtc = DateTime.UtcNow + loopFor;

            if (file == null)
            {
                fallbackBeep();
                return;
            }

            ensurePlayer();
            _player.Volume = toVolume(volumePercent);
            _player.Open(new Uri(file, UriKind.Absolute));
            _player.Play();
            setPlaying(true);
        }

        public static void SetVolume(int volumePercent)
        {
            if (_player != null) _player.Volume = toVolume(volumePercent);
        }

        public static void Stop()
        {
            _gap?.Stop();
            _loopUntilUtc = DateTime.MinValue;
            if (_player != null)
            {
                _player.Stop();
                _player.Close();
            }
            setPlaying(false);
        }

        private static void ensurePlayer()
        {
            if (_player != null) return;

            _player = new MediaPlayer();
            _player.MediaEnded += (s, e) =>
            {
                if (DateTime.UtcNow < _loopUntilUtc)
                {
                    _gap.Start(); // replay after a short pause
                }
                else
                {
                    _player.Close();
                    setPlaying(false);
                }
            };
            _player.MediaFailed += (s, e) =>
            {
                // file it can't decode: at least make a noise
                _player.Close();
                setPlaying(false);
                fallbackBeep();
            };

            _gap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _gap.Tick += (s, e) =>
            {
                _gap.Stop();
                if (_player == null || DateTime.UtcNow >= _loopUntilUtc) { Stop(); return; }
                _player.Position = TimeSpan.Zero;
                _player.Play();
            };
        }

        private static void setPlaying(bool playing)
        {
            if (_playing == playing) return;
            _playing = playing;
            PlayingChanged?.Invoke(null, EventArgs.Empty);
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private static void fallbackBeep() => PlaySound("SystemAsterisk", IntPtr.Zero, 0x00010000 | 0x0001);
    }
}
