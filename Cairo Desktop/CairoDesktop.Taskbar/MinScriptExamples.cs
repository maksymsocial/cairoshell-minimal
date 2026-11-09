using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CairoDesktop.Taskbar
{
    /// <summary>The scripts folder: example scripts, a template for new ones, opening and editing.</summary>
    internal static class MinScriptExamples
    {
        public static string Folder => MinScriptRunner.Folder;

        /// <summary>Creates the folder with the examples the first time.</summary>
        public static void EnsureFolder()
        {
            if (Directory.Exists(Folder)) return;
            try
            {
                Directory.CreateDirectory(Folder);
                write("README.txt", Readme);
                write("cpu-ram.ps1", CpuRam);
                write("counter.ps1", Counter);
                write("disk.nu", Disk);
            }
            catch (Exception) { }
        }

        /// <summary>Writes the examples again (overwrites them, leaves other scripts alone).</summary>
        public static void RestoreExamples()
        {
            Directory.CreateDirectory(Folder);
            write("README.txt", Readme);
            write("cpu-ram.ps1", CpuRam);
            write("counter.ps1", Counter);
            write("disk.nu", Disk);
        }

        private static void write(string name, string text)
        {
            // BOM for .ps1 so Windows PowerShell reads non-ASCII right; none for the rest (nu chokes on it)
            bool bom = name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(Path.Combine(Folder, name), text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(bom));
        }

        public static string[] Scripts()
        {
            EnsureFolder();
            try
            {
                return Directory.GetFiles(Folder)
                    .Where(f => !Path.GetFileName(f).Equals("README.txt", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception) { return new string[0]; }
        }

        /// <summary>Creates a new script from the template and returns its file name.</summary>
        public static string CreateNew(string name, string extension)
        {
            EnsureFolder();
            string clean = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            if (clean.Length == 0) clean = "my-item";
            string file = clean + extension;
            for (int i = 2; File.Exists(Path.Combine(Folder, file)); i++) file = clean + "-" + i + extension;

            string body;
            switch (extension)
            {
                case ".nu": body = TemplateNu; break;
                case ".py": body = TemplatePy; break;
                case ".bat": body = TemplateBat; break;
                default: body = TemplatePs1; break;
            }
            write(file, body.Replace("{name}", clean));
            return file;
        }

        public static void OpenFolder()
        {
            EnsureFolder();
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Folder + "\"") { UseShellExecute = true })?.Dispose(); }
            catch (Exception) { }
        }

        public static void Edit(string path)
        {
            if (!File.Exists(path)) return;
            // VS Code if there is one, else Notepad (the "edit" verb of .ps1 is the slow ISE)
            string code = findOnPath("code.cmd");
            try
            {
                if (code != null)
                    Process.Start(new ProcessStartInfo(code, "\"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                else
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception) { }
        }

        private static string findOnPath(string exe)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
                catch (Exception) { }
            }
            return null;
        }

        #region Texts
        private const string Readme = @"Custom bar items
================

Every file here can be shown on the bar: right-click the bar > Items... > Add > Script.
The bar runs it and shows what it prints. Save the file and the bar picks up the change.

Runs .ps1 (Windows PowerShell), .nu (Nushell), .py (Python), .js (Node), .bat/.cmd, .exe,
or anything else with ""# run: COMMAND {file}"" in its header.

Header (comment lines near the top, all optional)
  # interval: 5        seconds between runs (also 500ms, 1m). 0 = only when clicked.
  # mode: stream       keep running; every printed line is one update (cheaper for fast updates)
  # timeout: 10        seconds before a run is killed (poll mode)
  # run: pwsh -File {file}    run it with something else
  # name: CPU          name shown in the Items list

Output
  Plain text: first line is shown, the other lines are the tooltip.
  Or tags (genmon style), all optional, in any order:
    <txt>text</txt>          text on the bar
    <tool>tooltip</tool>     tooltip (can be several lines)
    <img>file</img>          icon: png/ico/jpg, or any exe/file to use its icon
    <bar>42</bar>            small vertical meter, 0-100
    <fg>#RRGGBB</fg>         text color        <bg>#RRGGBB</bg>  background
    <click>command</click>   run on left click (cmd line, e.g. taskmgr or start ms-settings:)
    <mclick>command</mclick> run on middle click
    <menu>Label|command</menu>  right-click menu entry (repeat it). Without |command
                                the script gets the event ""menu Label"".  <menu>-</menu> = line
    <interval>2</interval>   change the interval from inside the script
    <width>60</width>        minimum width in pixels (stops the bar jumping around)
    <hide>1</hide>           hide the item for now
  Use &lt; &gt; &amp; for < > & inside a tag.

Events
  Without a <click> tag a click is sent to the script instead:
    click left, click middle, scroll up, scroll down, menu LABEL
  Poll mode: the script runs right away with the event as arguments and in %MINBAR_EVENT%.
  Stream mode: the event is written to the script's stdin as one line.

Other environment variables: MINBAR_SCRIPT (this file), MINBAR_MODE (poll or stream).
";

        private const string CpuRam = @"# CPU and memory use. Click opens Task Manager.
# name: CPU / RAM
# mode: stream
# Stream mode: PowerShell starts once and keeps printing, instead of starting every 2 seconds.

while ($true) {
    $cpu = (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
    $os = Get-CimInstance Win32_OperatingSystem
    $total = $os.TotalVisibleMemorySize / 1MB
    $used = $total - $os.FreePhysicalMemory / 1MB
    $ram = [math]::Round(100 * $used / $total)
    $tip = 'CPU {0}%&#10;Memory {1:N1} / {2:N1} GB' -f $cpu, $used, $total
    ""<txt>cpu $cpu%  ram $ram%</txt><bar>$cpu</bar><tool>$tip</tool><click>taskmgr</click><width>110</width>""
    Start-Sleep -Seconds 2
}
";

        private const string Counter = @"# A counter that shows how clicks reach a script.
# Click or scroll up: +1, scroll down: -1, middle-click or right-click > Reset: 0.
# name: Counter
# interval: 0
# Interval 0: it only runs when it gets an event (or the bar starts).

$file = Join-Path $env:TEMP 'minbar-counter.txt'
$n = 0
if (Test-Path $file) { $n = [int](Get-Content $file) }

switch ($env:MINBAR_EVENT) {
    'click left'   { $n++ }
    'scroll up'    { $n++ }
    'scroll down'  { $n-- }
    'click middle' { $n = 0 }
    'menu Reset'   { $n = 0 }
}

Set-Content $file $n
""<txt>count $n</txt><tool>Click or scroll to change it</tool><menu>Reset</menu>""
";

        private const string Disk = @"# Free space on C:, refreshed every minute. Click opens This PC.
# name: Disk C:
# interval: 60

let d = (sys disks | where mount == 'C:\' | first)
let free = ($d.free / 1GB | math round)
let used = (100 - $d.free * 100 / $d.total | math round)
print $""<txt>C: ($free)G</txt><bar>($used)</bar><tool>($free) GB free of ($d.total / 1GB | math round) GB</tool><click>explorer.exe shell:MyComputerFolder</click>""
";

        private const string TemplatePs1 = @"# {name}
# name: {name}
# interval: 5
#
# Print what to show. Plain text works, or tags:
#   <txt> <tool> <img> <bar> <fg> <bg> <click> <mclick> <menu> <width> <hide>
# Clicks without a <click> tag come in as $env:MINBAR_EVENT (click left, scroll up, menu NAME, ...).
# See README.txt in this folder for everything.

$now = Get-Date
""<txt>$($now.ToString('ddd d MMM'))</txt><tool>Week $(Get-Date -UFormat %V)</tool>""
";

        private const string TemplateNu = @"# {name}
# name: {name}
# interval: 5
#
# Print what to show. Plain text works, or tags:
#   <txt> <tool> <img> <bar> <fg> <bg> <click> <mclick> <menu> <width> <hide>
# Clicks without a <click> tag come in as $env.MINBAR_EVENT (click left, scroll up, menu NAME, ...).
# See README.txt in this folder for everything.

print $""<txt>(date now | format date '%a %d %b')</txt><tool>(sys host | get hostname)</tool>""
";

        private const string TemplatePy = @"# {name}
# name: {name}
# interval: 5
#
# Print what to show. Plain text works, or tags:
#   <txt> <tool> <img> <bar> <fg> <bg> <click> <mclick> <menu> <width> <hide>
# Clicks without a <click> tag come in as os.environ['MINBAR_EVENT'] (click left, scroll up, ...).
# See README.txt in this folder for everything.
import datetime, os

now = datetime.datetime.now()
print(f""<txt>{now:%a %d %b}</txt><tool>{os.environ.get('MINBAR_EVENT') or 'no event'}</tool>"")
";

        private const string TemplateBat = @":: {name}
:: name: {name}
:: interval: 5
::
:: Print what to show. Plain text works, or tags (escape < > as ^< ^> in echo):
::   <txt> <tool> <img> <bar> <fg> <bg> <click> <mclick> <menu> <width> <hide>
:: Clicks without a <click> tag come in as %MINBAR_EVENT%.
@echo off
echo ^<txt^>%USERNAME%^</txt^>^<tool^>%COMPUTERNAME%^</tool^>
";
        #endregion
    }
}
