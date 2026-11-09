# Bar items and scripts

The bar is a list of items, left to right, like an xfce panel. Edit it with right-click on the bar > **Items...**, or right-click any added item on the bar to move or remove it.

| Item | What it is |
|---|---|
| Start button, Window buttons, Tray icons, User, Clock, Show desktop | the built-in parts (each once) |
| Separator | a thin line |
| Spacer | takes all free room, so everything after it goes to the right |
| Space | a fixed gap (4 / 8 / 16 / 32 px, "Wider" cycles) |
| Launcher | an app, shortcut, file or folder, shown as its icon |
| Script | any command whose output is shown on the bar (like xfce's generic monitor) |

The window buttons also fill the free room. With no spacer and no window buttons, items pack to the left.

## Scripts

Scripts live in `%LOCALAPPDATA%\Cairo Desktop\MinTaskbarScripts` (Items > Open scripts folder). The first time, it gets three examples: `cpu-ram.ps1`, `counter.ps1` and `disk.nu`, plus a `README.txt` with this reference. **Items > Add > New script** makes one from a template and opens it in an editor. Save the file, and the bar picks the change up at once.

How a script is run:

| File | Run with |
|---|---|
| `.ps1` | Windows PowerShell |
| `.nu` | Nushell |
| `.py` | `python -u` |
| `.js` | Node |
| `.bat`, `.cmd` | cmd |
| `.exe` | itself |
| anything else | its file association, or `# run:` |

Script processes run hidden, and they are killed when the bar closes, even if it crashes.

### Header

Comment lines near the top (`#`, `//`, `::`, `rem`, `--` or `;`). All of them are optional.

```
# name: CPU                 name in the Items list
# interval: 5               seconds between runs (also 500ms, 1m). 0 = only when clicked
# mode: stream              keep running; each printed line is one update
# timeout: 10               kill a run after this long (poll mode)
# run: pwsh -File {file}    run it with something else
```

**Poll mode** (the default) runs the script every interval and shows its whole output. **Stream mode** starts it once. Every line it prints is an update, which suits fast updates better than starting PowerShell every second.

### Output

Plain text: the first line is shown and the rest becomes the tooltip. For more control, use tags (genmon style). They're all optional and can go in any order:

| Tag | Effect |
|---|---|
| `<txt>text</txt>` | text on the bar |
| `<tool>text</tool>` | tooltip (`&#10;` for a new line in stream mode) |
| `<img>file</img>` | icon: png/ico/jpg, or any exe or file to use its icon |
| `<bar>42</bar>` | small vertical meter, 0–100 |
| `<fg>#RRGGBB</fg>` / `<bg>#RRGGBB</bg>` | text / background color |
| `<click>command</click>` | command line to run on left click, e.g. `taskmgr` or `start ms-settings:` |
| `<mclick>command</mclick>` | the same, for middle click |
| `<menu>Label\|command</menu>` | right-click menu entry; repeat it for more. Without `\|command`, the script gets `menu Label`. `<menu>-</menu>` is a separator line |
| `<interval>2</interval>` | change the interval from the script |
| `<width>60</width>` | minimum width, so the bar doesn't jump as the text changes |
| `<hide>1</hide>` | hide the item for now |

Escape `<`, `>` and `&` inside a tag as `&lt;`, `&gt;` and `&amp;`.

### Events

When there is no `<click>`, clicks go to the script as `click left`, `click middle`, `scroll up`, `scroll down` and `menu LABEL`.

- **Poll mode:** the script runs at once with the event as its arguments and in `MINBAR_EVENT`. If a run is still going, only the latest event is kept.
- **Stream mode:** the event is written to the script's stdin as one line.

Other variables: `MINBAR_SCRIPT` (the file) and `MINBAR_MODE` (`poll` or `stream`).

A script that fails shows `⚠ name: error` with the details in its tooltip. Click it to try again.

### Example

```powershell
# name: Clipboard length
# interval: 2
$n = (Get-Clipboard -Raw).Length
"<txt>clip $n</txt><tool>Characters on the clipboard</tool><menu>Open Notepad|notepad</menu>"
```
