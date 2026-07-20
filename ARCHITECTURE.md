# Architecture

## Overview

The application is a tray-hosted WinForms app with **no main window**. It observes the
keyboard with a global low-level hook, recognises the layout-switch gestures (a clean
`Ctrl+Shift` or `Alt+Shift` chord — whichever the system is configured for, read live
from the registry — plus the hardwired `Win+Space`), then (after a short delay) reads
the *actually active* layout of the foreground window's thread and shows a small
layered popup near the caret (or the mouse cursor).

```
                 ┌────────────────────────────────────────────────────────────┐
                 │                     TrayApplicationContext                   │
                 │   (coordination · timers · tray menu · request lifecycle)    │
                 └───┬───────────────┬───────────────┬──────────────┬──────────┘
                     │               │               │              │
          ┌──────────▼──────┐ ┌──────▼───────┐ ┌─────▼────────┐ ┌───▼────────────┐
          │ GlobalKeyboard  │ │ InputLanguage│ │ CaretLocator │ │ LanguagePopup  │
          │ Hook            │ │ Service      │ │ (cascade)    │ │ Form (layered) │
          │ (WH_KEYBOARD_LL)│ │ HKL → code   │ └─────┬────────┘ └────────────────┘
          └──────────┬──────┘ └──────────────┘       │
                     │                          ┌────┴──────────────┬────────────────┐
          ┌──────────▼──────────┐        ┌──────▼──────┐  ┌─────────▼───────┐ ┌──────▼───────┐
          │ LayoutHotkeyGesture │        │ Win32Caret  │  │ UiAutomation    │ │ CursorFallback│
          │ Detector (pure FSM) │        │ Locator     │  │ CaretLocator    │ │ Locator       │
          └─────────────────────┘        │ GUIThreadInfo│  │ (COM, MTA thread)│ │ GetCursorPos │
                                         └─────────────┘  └─────────────────┘ └──────────────┘

          Supporting:  SystemHotkeyService · PopupPositionService · SettingsService · StartupService · Logger · NativeMethods · Uia (COM interop)
```

## Components and responsibilities

| Component | Responsibility |
| --------- | -------------- |
| `Program` | Entry point, single-instance mutex, high-DPI mode, global exception handlers. |
| `TrayApplicationContext` | Wires services together; owns the tray icon/menu, timers, request lifecycle, and cleanup. The only place with orchestration logic. |
| `GlobalKeyboardHook` | Installs/removes `WH_KEYBOARD_LL`; converts native events into `KeyDown`/`KeyUp`. **Never suppresses** keys; no business logic. |
| `LayoutHotkeyGestureDetector` | Pure, Win32-independent state machine that recognises the layout-switch gestures: clean `Ctrl+Shift` / `Alt+Shift` chords and `Win+Space` (incl. `Win+Shift+Space`). Reports *which* gesture completed; auto-discards state older than 10 s. Fully unit-tested. |
| `SystemHotkeyService` | Reads `HKCU\Keyboard Layout\Toggle` (per completed gesture, off the hot path) to decide which chord actually switches layouts on this system — settings changes apply without restart. |
| `InputLanguageService` | Foreground window → thread id → `GetKeyboardLayout`; converts the HKL to a display code (`RU`/`EN`/`LT`/ISO). Resolves the layout-owning window for consoles (`ImmGetDefaultIMEWnd`) and UWP/Steam hosts (focus child). Composes the popup text, appending `CAPS` when CapsLock is on. |
| `CaretLocator` | Cascades the three position strategies and returns a screen rectangle + its source. |
| `Win32CaretLocator` | Strategy 1 — system caret via `GetGUIThreadInfo` + `ClientToScreen`. Skipped for Qt/Telegram windows, whose system caret is bogus. |
| `MsaaCaretLocator` | Strategy 2 — MSAA (`oleacc`) `AccessibleObjectFromWindow(OBJID_CARET)` + `IAccessible::accLocation`, queried against the thread's focus/caret window. Cheap; covers many Chromium/Electron apps where the system caret is absent. |
| `UiAutomationCaretLocator` | Strategy 3 — COM UI Automation `TextPattern2.GetCaretRange`, on a dedicated MTA worker thread. Single-flight (one request at a time), per-call timeout and a post-timeout cooldown so a hung provider degrades to the cursor instead of piling up work. Honours the caret's `isActive` flag and expands a degenerate range to a character to get a rectangle. |
| `CursorFallbackLocator` | Strategy 4 — mouse cursor via `GetCursorPos`. |
| `PopupPositionService` | Turns a caret rectangle into a final physical-pixel placement: per-monitor DPI, configured offsets, work-area clamping (handles negative coordinates). |
| `LanguagePopupForm` | Display-only borderless, click-through, non-activating **layered** popup (per-pixel alpha via `UpdateLayeredWindow`). Reused across shows. |
| `SettingsService` / `AppSettings` | Load/save JSON settings with defaults + clamping. |
| `StartupService` | Optional autostart via `HKCU\...\Run` (per-user, no admin). |
| `Logger` | Lightweight rotating file logger. Never logs keystrokes. |
| `NativeMethods` / `Uia` / `Msaa` | All P/Invoke and COM interop declarations; no logic. |

## Threading model

* **Hook callback** — `WH_KEYBOARD_LL` callbacks are dispatched *on the installing
  thread* (our UI thread, which pumps messages), not on a separate system thread.
  The callback still does *only* cheap work — update the gesture state machine and,
  on a recognised chord, `BeginInvoke` (defer to a later message-loop iteration) —
  because it must return to `CallNextHookEx` within the OS low-level-hook timeout.
  No UI Automation, no caret lookup, no blocking calls. The delegate is held in a
  field so the GC cannot collect it. The gesture detector auto-discards state older
  than 10 s, so key-ups lost to the secure desktop (UAC) cannot leave modifiers
  virtually held. `SystemEvents.SessionSwitch` (lock/unlock, RDP, fast user switch)
  additionally resets the detector, marshalled onto the UI thread.
* **UI thread** — starts the detection sequence, assigns each chord a monotonically
  increasing **request id**, cancels the previous request's `CancellationTokenSource`,
  and shows/updates the popup. UI mutations happen only here, via `Control.BeginInvoke`
  (never synchronous `Invoke` from the hook thread).
* **Thread-pool** — the two delayed layout/caret probes run here (`ConfigureAwait(false)`),
  so the UI thread is never blocked while a probe (which may call into UI Automation)
  is in progress.
* **UI Automation MTA worker** — a single long-lived background thread owns the COM
  `IUIAutomation` object. An in-process COM call cannot be forcibly aborted, so the
  worker uses *containment*: a single-flight gate means at most one request is ever
  in flight (extras return immediately → cursor fallback, so the queue never grows);
  the caller waits at most the timeout; and after a timeout UI Automation is skipped
  for a short cooldown. A permanently hung provider therefore just keeps UI
  Automation disabled instead of leaking work items.

**Staleness protection:** each chord's request id is checked again on the UI thread
before the popup is shown, and a newer chord cancels the previous request's token, so an
outdated probe can never overwrite a newer result or spawn a second popup.

## Layout detection timing

After a recognised chord the app waits `firstLayoutCheckDelayMs` (default 50 ms), reads
the layout/caret and shows the popup, then waits until `secondLayoutCheckDelayMs`
(default 140 ms) and re-reads. If the layout changed between the two reads (Windows
finished switching a little later) the popup text/position is updated in place; if
nothing changed the second read is a no-op (the hide timer is not restarted).

## DPI / multi-monitor

The process is **Per-Monitor V2** DPI aware (declared in `app.manifest`, mirrored by
`Application.SetHighDpiMode`). All screen coordinates are treated as physical pixels.
`PopupPositionService` selects the monitor under the anchor with `MonitorFromPoint`
(using an anchor stepped one pixel inside the exclusive caret rectangle so a caret on a
monitor's last column/row does not pick the neighbour), reads its work area
(`GetMonitorInfo`), scales the popup size and offsets, and clamps the result to that
monitor's work area — correctly handling monitors placed left of / above the primary
(negative coordinates). DPI for caret sources comes from `GetDpiForWindow` on the
foreground window (the API Microsoft recommends for Per-Monitor-V2 processes), falling
back to `GetDpiForMonitor` for the cursor case. The pure geometry lives in
`ComputeLocation` and is covered by unit tests (mixed DPI, edge flips, negative-origin
monitors).

## The popup window

`LanguagePopupForm` is a layered tool window with extended styles
`WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT`
and `ShowWithoutActivation => true`. It is drawn to a 32-bpp ARGB bitmap (rounded dark
translucent panel, white centred text) and presented with `UpdateLayeredWindow`, then
positioned with `SetWindowPos(..., SWP_NOACTIVATE | SWP_SHOWWINDOW)`. It never takes
focus, is invisible to `Alt+Tab` and the taskbar, and clicks pass through it. The fade-out
re-blits the same bitmap at a decreasing constant alpha (cheap; no re-render). The single
instance is created once at startup and reused for every show.

---

## Win32 / COM APIs used

**Keyboard hook** (`user32`, `kernel32`)
`SetWindowsHookEx` (`WH_KEYBOARD_LL`), `UnhookWindowsHookEx`, `CallNextHookEx`,
`GetModuleHandle`, `KBDLLHOOKSTRUCT`, `LLKHF_INJECTED`.

**Foreground window / layout** (`user32`, `imm32`)
`GetForegroundWindow`, `GetWindowThreadProcessId`, `GetKeyboardLayout`, `GetClassName`,
`ImmGetDefaultIMEWnd` (console layout), `GetGUIThreadInfo` focus window (UWP layout).

**Layout → code** (BCL) `CultureInfo`, LANGID extracted from the low word of the HKL.

**CapsLock** (`user32`) `GetKeyState(VK_CAPITAL)`.

**System caret** (`user32`)
`GetGUIThreadInfo` (`GUITHREADINFO`, `hwndCaret`, `rcCaret`), `ClientToScreen`.

**MSAA caret** (`oleacc`, hand-written COM interop)
`AccessibleObjectFromWindow(OBJID_CARET)` → `IAccessible::accLocation` (vtable slot 22).

**Mouse fallback** (`user32`) `GetCursorPos`.

**Monitors / DPI** (`user32`, `shcore`)
`MonitorFromPoint`, `MonitorFromWindow`, `GetMonitorInfo` (`MONITORINFO`, `rcWork`),
`GetDpiForMonitor`, `GetDpiForWindow`.

**Layered popup** (`user32`, `gdi32`)
`UpdateLayeredWindow` (`BLENDFUNCTION`, `AC_SRC_ALPHA`, `ULW_ALPHA`), `SetWindowPos`
(`SWP_NOACTIVATE`/`SWP_SHOWWINDOW`/`SWP_HIDEWINDOW`), `GetDC`/`ReleaseDC`,
`CreateCompatibleDC`/`DeleteDC`, `SelectObject`, `DeleteObject`, `DestroyIcon`.

**UI Automation (COM interop, hand-written)** — CLSID `CUIAutomation`, interfaces
`IUIAutomation::GetFocusedElement`, `IUIAutomationElement::GetCurrentPatternAs`,
`IUIAutomationTextPattern2::GetCaretRange`, `IUIAutomationTextRange::GetBoundingRectangles`
and `::ExpandToEnclosingUnit`. (The managed `System.Windows.Automation` port on .NET does
**not** expose `TextPattern2`, so the COM API is used directly.)

**Autostart** (BCL) `Microsoft.Win32.Registry` — `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

**System hotkey config** (BCL) `Microsoft.Win32.Registry` — `HKCU\Keyboard Layout\Toggle`
(read-only: `Language Hotkey` / `Layout Hotkey` / legacy `Hotkey`; `1` = Alt+Shift,
`2` = Ctrl+Shift, `3` = none, `4` = grave — unsupported).

Explicitly **not** used: `RegisterHotKey`, `ActivateKeyboardLayout`, `LoadKeyboardLayout`,
`GetKeyboardLayout(0)`, DLL injection, or any keystroke suppression.

---

## Acceptance-criteria verification report

Legend: **Auto** = verified programmatically in this session · **Design** = expected from
the code and reviewed, but not runtime-verified here · **Manual** = requires an interactive
desktop session (recommended check for the user).

| # | Criterion | Status | Notes |
|---|-----------|--------|-------|
| 1 | Windows `Ctrl+Shift` layout switching still works | Design | Hook returns `CallNextHookEx` and never suppresses; no `RegisterHotKey`. Confirm interactively. |
| 2 | App does not reserve the combo | Design/Auto | Low-level hook is observational; app launched and coexists. Confirm interactively. |
| 3 | Clean `Ctrl+Shift` shows the active layout | Auto/Manual | Gesture logic unit-tested; layout read + COM caret validated by harness. Full end-to-end is Manual. |
| 4 | If Windows did not switch, the old layout is shown | Design | The app only *reads* the current layout; it never assumes a change. |
| 5 | `Ctrl+Shift+<other key>` shows nothing | Auto | Unit tests: `+S`, `+T`, `+Esc`, and third-key-before-release all produce **no** event. |
| 6 | Popup does not steal focus | Design | `WS_EX_NOACTIVATE` + `ShowWithoutActivation` + `SWP_NOACTIVATE`. Confirm interactively. |
| 7 | In Notepad, popup appears at the caret | Design/Manual | `GetGUIThreadInfo` path; classic EDIT control exposes a caret. Manual check recommended. |
| 8 | If caret unavailable, popup appears at the mouse | Design | `CursorFallbackLocator` always succeeds. Manual check recommended. |
| 9 | Correct on multi-monitor with mixed DPI | Design/Manual | Per-Monitor V2 + per-monitor DPI/work-area math (incl. negative coords). Manual check recommended. |
| 10 | Repeated switches do not create multiple popups | Design/Auto | Single reused form; request-id + `CancellationTokenSource` supersede stale requests. |
| 11 | Hook removed cleanly on exit | Auto/Design | `Exit` → `ExitThread` → `Dispose` calls `UnhookWindowsHookEx`; logged. |
| 12 | No administrator rights required | Auto | Manifest `asInvoker`; runs as normal user; autostart under `HKCU`. |
| 13 | Negligible CPU at idle | Design | Event-driven; no polling loops; timers only during a ~1 s popup. Manual check recommended. |
| 14 | No leaks of windows, hooks, COM objects, timers | Design | Reviewed: hook unhooked; COM RCWs released (`Marshal.ReleaseComObject`); GDI objects freed; timers/tray disposed; `SystemEvents` unsubscribed; MTA worker joined. Not measured with a leak profiler. |

### Verified in this session (automated)

* Solution builds with **0 warnings / 0 errors** (Debug and Release), CI green on
  `windows-latest`.
* **55 unit tests** pass: the gesture state machine (Ctrl+Shift / Alt+Shift / Win+Space,
  cancellation incl. a key held *before* the chord, auto-repeat, staleness), system-hotkey
  interpretation, popup positioning (mixed DPI, edge flips, negative-origin monitors),
  settings normalization, and the CapsLock text composition.
* The **MSAA** (`accLocation`, vtable slot 22) and updated **UI Automation** interop were
  validated end-to-end against a focused control (real caret coordinates, no access
  violation).
* The app launches, installs the hook, writes logs, creates the correct camelCase
  `settings.json`, and **recovers from a corrupt settings file** (quarantines it and writes
  defaults — verified live); force-stop leaves a clean state.
* **Self-contained single-file x64 publish** succeeds (~50 MB `.exe`) and the published
  binary launches and installs the hook.
* The hand-written **COM UI Automation** caret path returns real caret coordinates
  (validated end-to-end against a focused rich-text control), confirming the vtable
  offsets are correct.

### Recommended manual checks (interactive desktop)

Criteria **1, 6, 7, 8, 9, 13** are best confirmed by using the app live: switch layouts
with `Ctrl+Shift` in Notepad / a browser / VS Code, on single- and multi-monitor setups at
100 %/125 %/150 %/200 % scaling, and observe focus, placement, and idle CPU.

---

## Acknowledgements

The caret-detection strategy (the MSAA `OBJID_CARET` step, the console/UWP layout-window
resolution, the Qt/Telegram guard, and the degenerate-range expansion) was informed by
[yakunins/language-indicator](https://github.com/yakunins/language-indicator) (MIT), an
AutoHotkey project with a different UX (per-language caret/cursor restyling). Its
in-process shellcode-injection caret method is deliberately **not** used here, per this
project's no-injection security constraint.
