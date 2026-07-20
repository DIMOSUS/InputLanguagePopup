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
| `DpiProbeWindow` | A tiny never-shown Per-Monitor-V2 window used only to read a monitor's true DPI (`GetDpiForWindow` on our own handle) without moving the visible popup. |
| `CaretLocator` | Cascades the three position strategies and returns a screen rectangle + its source. |
| `Win32CaretLocator` | Strategy 1 — system caret via `GetGUIThreadInfo` + `ClientToScreen`. Skipped for Qt/Telegram windows, whose system caret is bogus. |
| `AccessibilityCaretLocator` | Strategy 2 — MSAA then UI Automation on **one** long-lived MTA worker. MSAA: `AccessibleObjectFromWindow(OBJID_CARET)` + `IAccessible::accLocation` against the thread's focus/caret window (covers many Chromium/Electron apps). UIA: `TextPattern2.GetCaretRange` (honours `isActive`, expands a degenerate range). Both cross into the target process and can block, so the whole chain is wrapped in single-flight + per-call timeout + post-timeout cooldown — a hung provider degrades to the cursor and never accumulates blocked probes. |
| `MsaaCaretLocator` | Raw MSAA caret lookup (called on the accessibility worker). |
| `CursorFallbackLocator` | Strategy 3 — mouse cursor via `GetCursorPos`. |
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
  than 10 s (aged **per key**, refreshed by auto-repeat), so a key-up lost to the
  secure desktop (UAC) cannot leave that key virtually held — even while the user keeps
  typing; if one modifier of a chord expires, the chord rebases onto the still-held
  modifiers rather than firing falsely. `SystemEvents.SessionSwitch` (lock/unlock, RDP,
  fast user switch) additionally resets the detector, marshalled onto the UI thread.
* **UI thread** — starts the detection sequence, assigns each chord a monotonically
  increasing **request id**, cancels the previous request's `CancellationTokenSource`,
  and shows/updates the popup. UI mutations happen only here, via `Control.BeginInvoke`
  (never synchronous `Invoke` from the hook thread).
* **Thread-pool** — the two delayed layout/caret probes run here (`ConfigureAwait(false)`),
  so the UI thread is never blocked while a probe (which may call into UI Automation)
  is in progress.
* **Accessibility MTA worker** — a single long-lived background thread runs the whole
  MSAA→UIA chain and owns the COM `IUIAutomation` object. Both MSAA and UIA cross into
  the target process and can block, and neither can be cancelled once in flight, so the
  chain uses *containment*: a single-flight gate means at most one request runs at a
  time (extras return immediately → cursor fallback, so the thread-pool never
  accumulates blocked probes and the queue never grows); the caller waits at most the
  timeout; and after a timeout the whole accessibility chain is skipped for a short
  cooldown. A permanently hung provider just keeps the chain disabled instead of leaking
  work items.

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
(negative coordinates). The DPI scale is read from a dedicated, **never-shown**
Per-Monitor-V2 helper window (`DpiProbeWindow`): park it on the anchor's monitor, then
`GetDpiForWindow` on that handle. This gives the target monitor's true effective DPI
regardless of the foreground app's DPI awareness — `GetDpiForWindow` on a foreign
(DPI-unaware or system-aware) window would report the wrong value — and, crucially, it
never touches the **visible** popup (probing that during the second layout check would
move it off its computed position). The pure geometry lives in `ComputeLocation` and is
covered by unit tests (mixed DPI, edge flips, negative-origin monitors).

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

**CapsLock** (`user32`) `GetKeyState(VK_CAPITAL)` at show time. On this message-pumping,
hook-owning thread it reliably reflects the global toggle state (verified empirically),
and the just-processed gesture keeps it fresh — so no separate state tracking is needed.

**System caret** (`user32`)
`GetGUIThreadInfo` (`GUITHREADINFO`, `hwndCaret`, `rcCaret`), `ClientToScreen`.

**MSAA caret** (`oleacc`, hand-written COM interop)
`AccessibleObjectFromWindow(OBJID_CARET)` → `IAccessible::accLocation` (vtable slot 22).

**Mouse fallback** (`user32`) `GetCursorPos`.

**Monitors / DPI** (`user32`)
`MonitorFromPoint`, `GetMonitorInfo` (`MONITORINFO`, `rcWork`), `GetDpiForWindow` (on the
popup's own Per-Monitor-V2 window).

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
* **60 unit tests** pass: the gesture state machine (Ctrl+Shift / Alt+Shift / Win+Space,
  cancellation incl. a key held *before* the chord, auto-repeat, per-key staleness —
  including a key stuck while the user keeps typing, partial-expiry rebasing both ways),
  system-hotkey interpretation, popup positioning (mixed DPI, edge flips, negative-origin
  monitors), settings normalization, and CapsLock text composition.
* The **MSAA** (`accLocation`, slot 22) + **UI Automation** chain on the bounded worker
  returns real caret coordinates; the **`DpiProbeWindow`** returns per-monitor DPI and does
  **not** move the visible popup during a second probe — both validated at runtime.
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
