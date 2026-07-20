# Input Language Popup

A tiny background (tray) application for Windows that shows the **currently active
keyboard layout** next to the text caret whenever you switch layouts from the
keyboard — with the toggle hotkey configured in Windows (**`Ctrl+Shift`** or
**`Alt+Shift`**, read live from the system settings so no restart is needed after
changing it) as well as the hardwired **`Win+Space`** switcher.

* It **does not** switch the language itself.
* It **does not** reserve, intercept, or block any of these combinations — Windows
  keeps handling the switch normally.
* It shows the layout that is *actually* active afterwards. If Windows decided not
  to switch, it shows the unchanged layout (it never assumes a change happened).

The indicator appears:

1. next to the **text caret** in the active application, or
2. next to the **mouse cursor** when the caret cannot be located.

```
 ┌────┐
 │ RU │      ┌────┐   ┌────┐
 └────┘      │ EN │   │ LT │
             └────┘   └────┘
```

---

## Requirements

* Windows 10 or Windows 11, **x64**
* To build: **.NET 10 SDK** (`dotnet --version` → `10.x`)
* To run the published binary: **nothing** — it is self-contained.

The app runs entirely as a normal user; **administrator rights are not required**.

---

## Build

```powershell
dotnet build InputLanguagePopup.sln -c Release
```

## Run (from source)

```powershell
dotnet run --project src\InputLanguagePopup\InputLanguagePopup.csproj -c Release
```

A tray icon (`AЯ`) appears. Right-click it for the menu:

| Item                | Action                                                    |
| ------------------- | --------------------------------------------------------- |
| **Enabled**         | Turn the indicator on/off (hook stays installed either way)|
| **Show test popup** | Show a test popup next to the mouse cursor                |
| **Start with Windows** | Add/remove an autostart entry (per-user, no admin)     |
| **Exit**            | Cleanly remove the hook and quit                          |

## Run the tests

```powershell
dotnet test InputLanguagePopup.sln -c Release
```

The unit tests cover the `Ctrl+Shift` gesture recognition logic (14 scenarios).

## Publish (self-contained, single-file, x64)

```powershell
.\publish.ps1
```

or directly:

```powershell
dotnet publish src\InputLanguagePopup\InputLanguagePopup.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

The result is a single `InputLanguagePopup.exe` (~50 MB) in `.\publish\` that runs on
any Windows 10/11 x64 machine without a pre-installed .NET runtime.

---

## Settings

Stored as JSON at:

```
%LocalAppData%\InputLanguagePopup\settings.json
```

If the file is missing or corrupt, defaults are used (and a fresh file is written).
There is no settings UI in this version — edit the JSON by hand and restart the app.

```json
{
  "enabled": true,
  "popupDurationMs": 850,
  "firstLayoutCheckDelayMs": 50,
  "secondLayoutCheckDelayMs": 140,
  "caretOffsetX": 8,
  "caretOffsetY": 8,
  "cursorOffsetX": 16,
  "cursorOffsetY": 20,
  "useUiAutomation": true,
  "handleWinSpace": true,
  "startWithWindows": false
}
```

| Field                     | Meaning                                                   |
| ------------------------- | --------------------------------------------------------- |
| `enabled`                 | Master on/off (mirrors the tray **Enabled** item)         |
| `popupDurationMs`         | How long the popup stays fully visible before fading      |
| `firstLayoutCheckDelayMs` | Delay after the chord before the first layout read        |
| `secondLayoutCheckDelayMs`| Delay for the second read (catches slow switches)         |
| `caretOffsetX/Y`          | Offset (logical px) of the popup from the caret           |
| `cursorOffsetX/Y`         | Offset (logical px) of the popup from the mouse cursor    |
| `useUiAutomation`         | Enable the UI Automation caret fallback                   |
| `handleWinSpace`          | Also show the indicator after `Win+Space`                 |
| `startWithWindows`        | Autostart entry (mirrors the tray item)                   |

Values are clamped to sane ranges on load.

## Logs

Stored at:

```
%LocalAppData%\InputLanguagePopup\logs\app.log
```

Rotated at ~1 MB, keeping at most 5 files (`app.log`, `app.log.1` … `app.log.4`).

Logged: startup/shutdown, hook install/remove, Win32/UI-Automation errors, cases where
the layout could not be resolved, and unhandled exceptions. **Individual keystrokes are
never logged** — this is not a keylogger.

---

## Known limitations

* Does not run on the **Secure Desktop** (UAC prompt) or the **Windows lock/login
  screen** — a normal-privilege hook is inactive there.
* The **text caret cannot be located in every application**. Apps that render text
  themselves and expose no system caret or accessibility provider (some games,
  terminals with custom rendering, certain custom controls) fall back to the
  **mouse cursor** position.
* Layouts are shown as a two-letter code. `RU`, `EN`, `LT` are handled explicitly;
  everything else uses the two-letter ISO code from `CultureInfo`. Distinguishing
  several layouts of the same language (e.g. *English US* vs *English UK*) is possible
  in the design but not surfaced in this version.
* The rare grave-accent (`` ` ``) layout hotkey (registry code 4, used for Thai and a
  few other languages) is not supported. Per-layout direct hotkeys (e.g. a
  `Ctrl+Shift+0` assignment under `HKCU\Control Panel\Input Method\Hot Keys`) are
  intentionally ignored — a chord with an extra key never triggers the indicator.
* Only one instance runs at a time (guarded by a named mutex).

---

## Documentation

* **[ARCHITECTURE.md](ARCHITECTURE.md)** — component overview, threading model, the
  full list of Win32 / COM APIs used, and the acceptance-criteria verification report.
