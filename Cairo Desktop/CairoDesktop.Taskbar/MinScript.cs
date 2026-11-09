using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CairoDesktop.Taskbar
{
    /// <summary>What a script last said it wants to show.</summary>
    internal sealed class MinScriptState
    {
        public string Text = "";
        public string Tooltip;
        public string Icon;
        public string Click;
        public string MiddleClick;
        public string Foreground;
        public string Background;
        public double? Bar;
        public double? Interval;
        public double? Width;
        public bool Hidden;
        public bool IsError;
        public readonly List<KeyValuePair<string, string>> Menu = new List<KeyValuePair<string, string>>();
    }

    /// <summary>Settings a script gives itself in comment lines near the top, e.g. "# interval: 2".</summary>
    internal sealed class MinScriptHeader
    {
        public double Interval = 5;
        public bool Stream;
        public double Timeout = 10;
        public string Run;
        public string Name;

        private static readonly Regex Line = new Regex(
            @"^\s*(?:#|//|::|--|;|rem\s)\s*(interval|mode|timeout|run|name)\s*[:=]\s*(.+?)\s*$",
            RegexOptions.IgnoreCase);

        public static MinScriptHeader Read(string path)
        {
            var h = new MinScriptHeader();
            try
            {
                foreach (string raw in File.ReadLines(path).Take(40))
                {
                    var m = Line.Match(raw);
                    if (!m.Success) continue;
                    string v = m.Groups[2].Value;
                    switch (m.Groups[1].Value.ToLowerInvariant())
                    {
                        case "interval":
                            if (MinScriptRunner.TryParseSeconds(v, out double i)) h.Interval = i;
                            break;
                        case "timeout":
                            if (MinScriptRunner.TryParseSeconds(v, out double t)) h.Timeout = t;
                            break;
                        case "mode":
                            h.Stream = v.Trim().Equals("stream", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "run":
                            h.Run = v;
                            break;
                        case "name":
                            h.Name = v;
                            break;
                    }
                }
            }
            catch (Exception) { }
            return h;
        }
    }

    /// <summary>
    /// Runs one script for every bar that shows it (shared, so two monitors don't run it twice).
    ///
    /// Poll mode (default): runs the script every "interval" seconds; its whole output is one update.
    /// Stream mode ("# mode: stream"): the script keeps running; every line it prints is one update,
    /// and clicks / scrolls are written to its stdin as lines ("click left", "scroll up", ...).
    /// In poll mode those events are passed as arguments and in MINBAR_EVENT.
    /// </summary>
    internal sealed class MinScriptRunner : IDisposable
    {
        public static string Folder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cairo Desktop", "MinTaskbarScripts");

        private static readonly Dictionary<string, MinScriptRunner> _all = new Dictionary<string, MinScriptRunner>(StringComparer.OrdinalIgnoreCase);

        public static string ResolvePath(string spec)
        {
            spec = Environment.ExpandEnvironmentVariables((spec ?? "").Trim().Trim('"'));
            return System.IO.Path.IsPathRooted(spec) ? spec : System.IO.Path.Combine(Folder, spec);
        }

        public static MinScriptRunner Acquire(string spec)
        {
            string path = ResolvePath(spec);
            if (!_all.TryGetValue(path, out var r))
            {
                r = new MinScriptRunner(path);
                _all[path] = r;
                r.start();
            }
            r._refs++;
            return r;
        }

        public void Release()
        {
            if (--_refs > 0) return;
            _all.Remove(Path);
            Dispose();
        }

        public readonly string Path;
        public MinScriptHeader Header { get; private set; }
        public MinScriptState State { get; private set; } = new MinScriptState { Text = "…" };
        public event EventHandler Updated;

        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _restartTimer;
        private FileSystemWatcher _watcher;
        private DispatcherTimer _reloadTimer;
        private Process _stream;
        private int _refs;
        private bool _running;
        private string _pendingEvent;
        private bool _hasPending;
        private bool _disposed;
        private int _quickExits;
        private DateTime _streamStarted;

        private MinScriptRunner(string path)
        {
            Path = path;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background);
            _pollTimer.Tick += (s, e) => { _pollTimer.Stop(); runOnce(null); };
            _restartTimer = new DispatcherTimer(DispatcherPriority.Background);
            _restartTimer.Tick += (s, e) => { _restartTimer.Stop(); startStream(); };
        }

        #region Lifetime
        private void start()
        {
            if (!File.Exists(Path))
            {
                setError("script not found", Path);
                watch();
                return;
            }

            Header = MinScriptHeader.Read(Path);
            watch();

            if (Header.Stream) startStream();
            else runOnce(null);
        }

        /// <summary>Re-reads the header and starts over (the script was edited).</summary>
        public void Restart()
        {
            if (_disposed) return;
            _pollTimer.Stop();
            _restartTimer.Stop();
            stopStream();
            _quickExits = 0;
            start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer.Stop();
            _restartTimer.Stop();
            _reloadTimer?.Stop();
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            stopStream();
        }

        private void watch()
        {
            if (_watcher != null) return;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Path);
                if (!Directory.Exists(dir)) return;
                _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                FileSystemEventHandler changed = (s, e) => _dispatcher.BeginInvoke(new Action(scheduleReload));
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Renamed += (s, e) => _dispatcher.BeginInvoke(new Action(scheduleReload));
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception) { }
        }

        private void scheduleReload()
        {
            if (_disposed) return;
            if (_reloadTimer == null)
            {
                // editors save in several steps: wait until it settles
                _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _reloadTimer.Tick += (s, e) => { _reloadTimer.Stop(); Restart(); };
            }
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }
        #endregion

        #region Events from the bar
        /// <summary>"click left", "click middle", "scroll up", "scroll down", "menu NAME".</summary>
        public void SendEvent(string ev)
        {
            if (_disposed || Header == null) return;

            if (Header.Stream)
            {
                try
                {
                    if (_stream != null && !_stream.HasExited)
                    {
                        _stream.StandardInput.WriteLine(ev);
                        _stream.StandardInput.Flush();
                    }
                }
                catch (Exception) { }
                return;
            }

            if (_running)
            {
                // only the latest event matters (e.g. fast scrolling)
                _pendingEvent = ev;
                _hasPending = true;
                return;
            }

            _pollTimer.Stop();
            runOnce(ev);
        }

        /// <summary>Runs the script again now (poll mode).</summary>
        public void Refresh()
        {
            if (_disposed || Header == null) return;
            if (Header.Stream)
            {
                if (_stream == null || _stream.HasExited) startStream();
                return;
            }
            if (_running) return;
            _pollTimer.Stop();
            runOnce(null);
        }
        #endregion

        #region Poll mode
        private void runOnce(string ev)
        {
            if (_disposed) return;
            _running = true;
            string path = Path;
            MinScriptHeader header = Header;

            Task.Run(() => execute(path, header, ev)).ContinueWith(t =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    _running = false;
                    if (_disposed) return;

                    var (output, error, code, timedOut) = t.Status == TaskStatus.RanToCompletion
                        ? t.Result
                        : (null, t.Exception?.GetBaseException().Message, -1, false);

                    if (timedOut) setError("timed out", $"Took longer than {header.Timeout:0.#} s (# timeout: N to change)");
                    else if (output == null || (string.IsNullOrWhiteSpace(output) && code != 0))
                        setError(code == -1 ? "can't run" : "error " + code, string.IsNullOrWhiteSpace(error) ? "No output" : error.Trim());
                    else apply(Parse(output));

                    if (_hasPending)
                    {
                        string next = _pendingEvent;
                        _hasPending = false;
                        _pendingEvent = null;
                        runOnce(next);
                        return;
                    }

                    double secs = State.Interval ?? header.Interval;
                    if (secs > 0)
                    {
                        _pollTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.2, secs));
                        _pollTimer.Start();
                    }
                }));
            });
        }

        private static (string output, string error, int code, bool timedOut) execute(string path, MinScriptHeader header, string ev)
        {
            var psi = startInfo(path, header, ev, false);
            using (var p = new Process { StartInfo = psi })
            {
                try { p.Start(); }
                catch (Exception e) { return (null, e.Message + "\n" + psi.FileName + " " + psi.Arguments, -1, false); }

                MinJob.Add(p);
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit((int)Math.Max(500, header.Timeout * 1000)))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return (null, null, -1, true);
                }

                p.WaitForExit(); // let the output readers finish
                return (outTask.Result, errTask.Result, p.ExitCode, false);
            }
        }
        #endregion

        #region Stream mode
        private void startStream()
        {
            if (_disposed) return;
            stopStream();

            var psi = startInfo(Path, Header, null, true);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            string lastError = null;

            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null || e.Data.Trim().Length == 0) return;
                string line = e.Data;
                _dispatcher.BeginInvoke(new Action(() => { if (!_disposed && _stream == p) apply(Parse(line)); }));
            };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data; };
            p.Exited += (s, e) => _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || _stream != p) return;
                int code = -1;
                try { code = p.ExitCode; } catch (Exception) { }
                _stream = null;

                // restart; back off if it keeps dying right away
                bool quick = DateTime.UtcNow - _streamStarted < TimeSpan.FromSeconds(5);
                _quickExits = quick ? _quickExits + 1 : 0;
                if (_quickExits >= 3) setError("stopped", (lastError ?? "Exited with code " + code) + "\nRestarts when the script is saved, or click to retry.");
                _restartTimer.Interval = TimeSpan.FromSeconds(_quickExits >= 3 ? 60 : 2);
                _restartTimer.Start();
            }));

            try
            {
                p.Start();
                MinJob.Add(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _stream = p;
                _streamStarted = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                setError("can't run", e.Message + "\n" + psi.FileName + " " + psi.Arguments);
            }
        }

        private void stopStream()
        {
            var p = _stream;
            _stream = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (Exception) { }
            p.Dispose();
        }
        #endregion

        #region Launching
        private static string _nuPath;

        private static string findNu()
        {
            if (_nuPath != null) return _nuPath;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = System.IO.Path.Combine(dir.Trim(), "nu.exe");
                    if (File.Exists(p)) return _nuPath = p;
                }
                catch (Exception) { }
            }
            string local = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nu", "bin", "nu.exe");
            return _nuPath = File.Exists(local) ? local : "nu.exe";
        }

        private static string quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        internal static ProcessStartInfo startInfo(string path, MinScriptHeader header, string ev, bool stream)
        {
            string args = ev ?? "";
            string file, arguments;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(header?.Run))
            {
                // "# run: python -u {file}"
                file = "cmd.exe";
                arguments = "/d /s /c \"" + header.Run.Replace("{file}", quote(path)) + (args.Length > 0 ? " " + args : "") + "\"";
            }
            else switch (ext)
            {
                case ".ps1":
                    file = "powershell.exe";
                    arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '"
                        + path.Replace("'", "''").Replace("\"", "\\\"") + "' " + args + "\"";
                    break;
                case ".nu":
                    file = findNu();
                    arguments = quote(path) + " " + args;
                    break;
                case ".py":
                case ".pyw":
                    file = "python.exe";
                    arguments = "-u " + quote(path) + " " + args;
                    break;
                case ".js":
                case ".mjs":
                    file = "node.exe";
                    arguments = quote(path) + " " + args;
                    break;
                case ".exe":
                    file = path;
                    arguments = args;
                    break;
                default: // .bat, .cmd and anything with a file association
                    file = "cmd.exe";
                    arguments = "/d /s /c \"" + quote(path) + (args.Length > 0 ? " " + args : "") + "\"";
                    break;
            }

            var psi = new ProcessStartInfo(file, arguments.Trim())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stream,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? Folder
            };
            psi.Environment["MINBAR_EVENT"] = ev ?? "";
            psi.Environment["MINBAR_SCRIPT"] = path;
            psi.Environment["MINBAR_MODE"] = stream ? "stream" : "poll";
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            return psi;
        }

        /// <summary>Runs a click command line from a script ("taskmgr", "start ms-settings:", ...).</summary>
        public static void RunCommand(string command, string workingDir)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                });
                p?.Dispose();
            }
            catch (Exception) { }
        }
        #endregion

        #region Output
        private static readonly Regex Tag = new Regex(@"<(\w+)>(.*?)</\1>", RegexOptions.Singleline);

        private static readonly HashSet<string> KnownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "tool", "img", "icon", "click", "txtclick", "mclick", "bar", "fg", "bg", "menu", "interval", "width", "hide"
        };

        /// <summary>
        /// Tags (genmon style, all optional): &lt;txt&gt; &lt;tool&gt; &lt;img&gt; &lt;click&gt; &lt;mclick&gt; &lt;bar&gt; &lt;fg&gt; &lt;bg&gt;
        /// &lt;menu&gt;Label|command&lt;/menu&gt; (repeat) &lt;interval&gt; &lt;width&gt; &lt;hide&gt;.
        /// Without tags: the first line is the text, the rest the tooltip.
        /// </summary>
        public static MinScriptState Parse(string output)
        {
            var st = new MinScriptState();
            output = (output ?? "").Replace("\r", "");
            var matches = Tag.Matches(output).Cast<Match>().Where(m => KnownTags.Contains(m.Groups[1].Value)).ToList();

            if (matches.Count == 0)
            {
                var lines = output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                st.Text = lines.FirstOrDefault() ?? "";
                if (lines.Count > 1) st.Tooltip = string.Join("\n", lines.Skip(1));
                return st;
            }

            foreach (var m in matches)
            {
                string v = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "txt": st.Text = v; break;
                    case "tool": st.Tooltip = v; break;
                    case "img":
                    case "icon": st.Icon = v; break;
                    case "click":
                    case "txtclick": st.Click = v; break;
                    case "mclick": st.MiddleClick = v; break;
                    case "fg": st.Foreground = v; break;
                    case "bg": st.Background = v; break;
                    case "hide": st.Hidden = v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                    case "bar":
                        if (double.TryParse(v.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
                            st.Bar = Math.Max(0, Math.Min(100, b));
                        break;
                    case "interval":
                        if (TryParseSeconds(v, out double i)) st.Interval = i;
                        break;
                    case "width":
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)) st.Width = w;
                        break;
                    case "menu":
                        int bar = v.IndexOf('|');
                        st.Menu.Add(bar < 0
                            ? new KeyValuePair<string, string>(v, null)
                            : new KeyValuePair<string, string>(v.Substring(0, bar).Trim(), v.Substring(bar + 1).Trim()));
                        break;
                }
            }
            return st;
        }

        /// <summary>"5", "2.5", "500ms", "2s", "1m", "1h".</summary>
        public static bool TryParseSeconds(string v, out double seconds)
        {
            seconds = 0;
            var m = Regex.Match((v ?? "").Trim(), @"^(\d+(?:[.,]\d+)?)\s*(ms|s|sec|m|min|h)?$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            double n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "ms": seconds = n / 1000; break;
                case "m":
                case "min": seconds = n * 60; break;
                case "h": seconds = n * 3600; break;
                default: seconds = n; break;
            }
            return true;
        }

        private void apply(MinScriptState st)
        {
            State = st;
            Updated?.Invoke(this, EventArgs.Empty);
        }

        private void setError(string text, string detail)
        {
            apply(new MinScriptState { Text = "⚠ " + System.IO.Path.GetFileName(Path) + ": " + text, Tooltip = detail, IsError = true });
        }
        #endregion
    }

    /// <summary>A job object that kills all script processes when Cairo exits, even if it crashes.</summary>
    internal static class MinJob
    {
        private static IntPtr _job;

        public static void Add(Process p)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr ptr = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(_job, 9, ptr, (uint)size); // JobObjectExtendedLimitInformation
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                AssignProcessToJobObject(_job, p.Handle);
            }
            catch (Exception) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
