# AutoAccept

A small always-on-top card for the corner of your screen. It watches your IDE (VS Code + Copilot, Cursor,
Windsurf, VSCodium, or your own IDE) and presses **Allow** on permission prompts after a 1-second countdown.
**Risky commands** (delete, `Remove-Item`, `rm`, format, force-push…) are **held for you** instead, and when
Copilot **asks you a question or offers choices**, it never answers. It notifies you and takes you to it in one click.

## Download and run (any Windows 10/11 PC)

1. Open the repo's **[Releases page](https://github.com/goki62/AutoAccept/releases/latest)** (sign in to GitHub
   first while the repo is private) and download **`AutoAccept.exe`**.
2. Put it in a folder of its own, e.g. `Documents\AutoAccept\`. It creates `settings.json` and `history.log` next to itself.
3. Double-click it. The first time, Windows may show **"Windows protected your PC"** because the exe isn't
   code-signed: click **More info → Run anyway**.
4. The card appears in the bottom-right corner. To have it start with Windows: click the card →
   **Settings** → *Start AutoAccept when Windows starts*.

Nothing to install: it runs on .NET Framework 4.8, which every Windows 10/11 PC already has.
To build it yourself instead: `git clone`, then `powershell -ExecutionPolicy Bypass -File build.ps1` (see *Build from source*).

```
 ┌──────────────────────────────────────────┐
 │ ●  AutoAccept                    16 today│   ← click: panel with activity, history, settings
 │    "Allow" in Code · 0.6s · click to skip│   ← hover: ⏸ pause and ✕ quit buttons
 │▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░│   ← countdown bar
 └──────────────────────────────────────────┘
```

| | |
|---|---|
| **What it is** | A Windows desktop app in **C# / WinForms** on .NET Framework 4.8 (built into Windows 10/11). Not WPF, not Blazor, no web runtime |
| **Size** | One `AutoAccept.exe` of ~125 KB. Nothing to install |
| **Footprint** | ~40 MB RAM, ~1–2 % of one CPU core while watching (measured) |
| **Touches** | Only buttons inside the apps you list, and in VS Code only inside the Copilot chat |
| **Records** | Every accepted / held / skipped prompt with the full command, in a History window and `autoaccept.log` |

---

## Files

```
AutoAccept\
├─ dist\                  ← the app, built by build.ps1 (not in git; download the exe from Releases)
│   ├─ AutoAccept.exe     ← the program (the only thing you run)
│   ├─ settings.json      ← your settings (created on first run; edit them in the Settings window)
│   └─ history.log        ← everything it accepted / held (shown in the History window)
├─ src\                   ← source code (see "Build from source")
├─ test\                  ← fake Copilot page for testing
├─ build\                 ← icon for the exe
├─ build.ps1              ← rebuilds dist\AutoAccept.exe
└─ README.md
```

The widget position is stored in the registry (`HKCU\Software\AutoAccept`), not in a file. Older
versions used `autoaccept.json` / `autoaccept.log` / `autoaccept.pos`; these are renamed automatically
on first start. **Only one copy ever runs:** starting `AutoAccept.exe` again opens the running copy's panel.

## Using it

Run `dist\AutoAccept.exe`. A notification points to the card in the bottom-right corner.

| Do this | What happens |
|---|---|
| **Click the card** | Opens the **panel**: status, an on/off switch, today's numbers, recent activity, and **History / Settings / Quit** buttons |
| **Hover the card** | Shows a **⏸ pause** and an **✕ quit** button |
| Click during a countdown | Skips that prompt (it stays skipped until it goes away) |
| Right-click the card or the tray icon | Full menu: pause, history, settings, scan report, log file, start with Windows, hide, quit |
| `Ctrl+Alt+P` | Pause / resume from anywhere |
| Drag the card | Moves it; the position is remembered |
| Run `AutoAccept.exe` again | Opens the panel of the running copy (no second copy starts) |

**To stop it:** hover the card → ✕, or panel → **Quit**, or right-click the tray icon → **Quit AutoAccept**,
or run `AutoAccept.exe --quit`.
**To pause it** without quitting: the switch in the panel, ⏸ on the card, or `Ctrl+Alt+P`.

Card colours: **green** watching · **amber** counting down / needs a restore · **blue** Copilot is asking
you something · **orange** risky command held for you · **grey** paused · **red** error. When it's blue or
orange, **clicking the card jumps straight to the IDE window**.

### When Copilot needs your decision
Sometimes Copilot asks a question instead of asking for permission ("Which database should I use?" with 3–4
options), or a prompt offers several choices. AutoAccept **never answers those**:
- Copilot's question cards (VS Code's `chat-question-carousel`) are detected on every scan, and nothing inside them is clicked.
- Any prompt with choices (radio buttons, checkboxes, a drop-down, or extra option buttons) counts as a
  decision, even if it also has an **Allow** button.

Instead you get:
- a Windows notification with the question;
- a sound (optional);
- the IDE's taskbar button flashing until you look (optional);
- a blue card reading **"Copilot asks: …"**.

Click the notification or the card and the IDE window comes to the front, restored if it was minimized,
so you can answer straight away. Every decision is listed in History under **Needs you**.

### History: what did it accept while I was away?
Panel → **History** (or `AutoAccept.exe --history`). It lists every prompt AutoAccept **accepted**,
**held for you**, **skipped** or **failed**, newest first:
- each row shows time, app, button and command;
- select a row to see the full command, the reason and the window title;
- you can filter (All / Accepted / Held / Skipped & failed) and search.

The History window is rebuilt from `history.log`, so it survives restarts. The log keeps about 5 MB,
plus one older file (`history.log.old`).

### Settings
Panel → **Settings** (or `AutoAccept.exe --settings`). No JSON editing needed:

| Setting | Default |
|---|---|
| Start watching when AutoAccept opens | on |
| Wait before clicking | 1.0 s |
| Also work while the IDE is minimized | on (see *Minimized windows* below) |
| Start AutoAccept when Windows starts | off |
| Pause / resume hotkey | `Ctrl+Alt+P` |
| VS Code: only press buttons inside the Copilot chat | on |
| Only press when a Skip / Cancel button sits next to it | on |
| Hold risky commands for me (+ the list of patterns) | on, 18 patterns |
| Also hold prompts whose text can't be read | off |
| Apps to watch | Code, Code - Insiders, Cursor, Windsurf, VSCodium |
| Buttons to press | Allow, Allow Once, Allow in this Session, Continue, Approve, Accept |

Saving writes `settings.json`, a commented JSON file next to the exe, and applies it straight away.
**Open settings.json…** opens that file for the advanced options: `pollMs`, `cooldownMs`,
`declineLabels`, `denyWords`, `titleContains`, `clickMode`.

---

## Will it press random buttons?

No. A button is pressed only when **all** of these hold:

1. It's in a window of an app on your list. Browsers, chat apps and system dialogs are never touched.
2. In VS Code, it's **inside the Copilot chat**. No other VS Code button or dialog is ever pressed.
3. Its label is exactly one of the accept labels, e.g. `Allow`. VS Code's `Allow (Ctrl+Enter)` counts:
   the keyboard hint is ignored.
4. A **Skip / Cancel / Deny** button sits right next to it. Real permission prompts always have one;
   toolbars, debugger buttons and so on don't.
5. Its label contains no word like delete, remove or overwrite.
6. The prompt's **command** matches none of the risky-command patterns. If it matches, the prompt is held for you.
7. It isn't a **question or a choice** (see *When Copilot needs your decision*). Those are always left to you.

Before the click it waits 1 second and checks everything again. Tested on a page full of decoys
(a lone `Continue`, a disabled `Allow`, `Delete files` next to `Cancel`): no decoy was ever pressed.

## The command filter
Before counting down, and again just before clicking, AutoAccept reads the prompt's text:
- **The prompt card:** the title, message, any visible command, and VS Code's risk summary
  ("Deletes a picture from the Desktop.").
- **VS Code chat:** VS Code draws the command in a code editor that UI Automation can't read, but the
  chat row that owns the prompt is labelled `Chat confirmation required: Run pwsh command?: <full command>`.
  AutoAccept reads that.

If a pattern matches, nothing is clicked. The card turns orange (**Needs you: "Remove-Item" in Code**),
a notification pops up, and History records it as *Held for you*. Tested on real commands from a Copilot
session: "list the pictures on the Desktop" passes, and the follow-up `…InvokeVerb('delete')…` is held.

> It matches patterns, so it can't understand everything. A script such as `.\cleanup.ps1` that deletes
> files won't match unless its name or VS Code's summary says so. Add your own patterns in Settings.

## Behind other windows, other monitors, minimized

- **Not focused / behind other windows / on another monitor: works.** It presses buttons through
  Windows UI Automation (Invoke), the interface screen readers use. That needs no mouse and no focus.
- **Minimized: partly.** Chromium, which VS Code is built on, often **stops updating** what it shows to
  accessibility tools once a window goes from visible to minimized. In testing, a window minimized
  *after* use stopped showing new prompts until it was restored, while a window that was minimized from
  the start kept working. AutoAccept detects both cases and the card says so, so it never fails silently:
  - **"Open Code once so I can see it"**: the window was minimized before AutoAccept could read it.
  - **"Code is minimized and stopped updating: restore it"**: a button it already pressed is still listed.

  It then **doesn't** press anything again. For reliable hands-off sessions, leave VS Code open behind
  your other windows (or on another monitor) instead of minimizing it.

---

## How it works

```
every 0.6 s:  find the windows of the watched apps (EnumWindows)
              └─ one cached UI Automation FindAll per window → enabled, visible Buttons (name, bounds, id)
                 └─ rules 1–5 above → candidate
                    └─ read the prompt text → risky? → hold for you (orange, notification, history)
                       └─ 1 s countdown on the card → re-check → InvokePattern.Invoke() → log + history
```

- **Why UI Automation, not screenshots/OCR:** it reads the exact button text, doesn't need the window
  in front, doesn't move your mouse, and doesn't care about theme, language or DPI.
- **Clicking:** `Invoke` first. Only if a button doesn't support it is a real mouse click used, and then
  only when the IDE window is really under the cursor. A button is never pressed twice.
- The card is a `WS_EX_NOACTIVATE` tool window, so clicking it never takes focus from your editor.
  It's per-monitor DPI aware.

## Command line

```powershell
AutoAccept.exe                          # start (or open the panel of the running copy)
AutoAccept.exe --settings               # open settings
AutoAccept.exe --history                # open history
AutoAccept.exe --quit                   # stop the running copy
AutoAccept.exe --scan [report.txt]      # list every button it can see and what it would do; no clicking
AutoAccept.exe --config D:\other.json   # use another settings file
```

Without a file name, `--scan` writes the report to your temp folder and opens it in Notepad.

## Adding your own IDE
1. Settings → **Apps to watch**: add its process name. Right-click → **Scan report…** lists the apps
   that have windows open.
2. Trigger one of its permission prompts, then run **Scan report…**:
   - `WOULD CLICK` means it will be pressed;
   - `NEEDS YOU` means the command filter will hold it;
   - `no decline button nearby` means you need to add your "no" button's label to `declineLabels` (Edit JSON).
3. If you're building the IDE: use real `<button>`s (or `role="button"`) labelled "Allow" / "Skip", and
   keep the command text readable (not only inside a canvas/editor). Better still, ship an auto-approve
   setting with allow and deny lists.

## Alternatives built into the tools
- **VS Code / Copilot:** the chat's **permission picker** ("Default permissions") and the `autoApprove`
  settings can auto-approve tools, with allow and deny lists for terminal commands.
- **Cursor / Windsurf:** their own auto-run / allow-list settings.

## Self-test
`test\permission-test.html` + `test\test-config.json` simulate Copilot prompts: two safe, two risky (one
with the command hidden VS Code-style), a Copilot question card, a multiple-choice prompt with an Allow
button, plus decoys. Open the page in an Edge `--app` window with
`--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows`
and run it as a separate, labelled copy next to your normal one:

```powershell
$env:AUTOACCEPT_INSTANCE = 'test'; .\dist\AutoAccept.exe --config test\test-config.json
```

Its card reads **AutoAccept (test)**. Expected page title:
`allowed=2 held=4 dangerAllowed=0 skipped=0 decoys=0 … DONE`. Stop it with
`$env:AUTOACCEPT_INSTANCE = 'test'; .\dist\AutoAccept.exe --quit`.

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This uses `csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`, which is on every Windows
install, so no SDK is needed. Output: `dist\AutoAccept.exe`.

```
src\
├─ Program.cs            entry point: command line, single instance, starts the scanner + card
├─ AppFiles.cs           file names (settings.json, history.log), single instance, Start-with-Windows,
│                        widget position
├─ Native.cs             Windows API declarations
├─ Engine\               finding and pressing buttons. No UI.
│   ├─ Scanner.cs        the loop: scan → countdown → re-check → click; skip, hold, stale detection
│   ├─ Uia.cs            UI Automation: list a window's buttons; find the watched windows
│   ├─ Rules.cs          label matching, deny words, "decline button nearby"
│   ├─ CommandFilter.cs  reads the prompt / command text and checks the risky-command patterns
│   ├─ Clicker.cs        Invoke, or a guarded mouse click
│   ├─ Model.cs          TargetWindow, ButtonInfo, Candidate
│   ├─ Config.cs         settings.json: defaults, load, validate, write (with comments)
│   ├─ History.cs        history entries + rebuilding them from history.log
│   └─ Report.cs         the scan report
└─ UI\                   everything you see
    ├─ Widget.cs         the corner card, tray icon, menu, hotkey, settings hot-reload
    ├─ PanelForm.cs      the panel that opens when you click the card
    ├─ HistoryForm.cs    the History window
    ├─ SettingsForm.cs   the Settings window
    ├─ Theme.cs          colours, fonts, drawing helpers
    └─ Logo.cs           the icon
```
