# AGENTS.md

## Overview

Key2Gif is a **WPF .NET 8 Windows desktop app** — a system-tray GIF picker. Press a global hotkey → a borderless popup appears near the caret → search GIPHY → click a GIF → it's pasted into whatever window had focus. Single project, no solution file, no tests.

## Build & Run

```sh
# Build (debug)
dotnet build Key2Gif/Key2Gif.csproj

# Run
dotnet run --project Key2Gif/Key2Gif.csproj

# Publish self-contained single-file exe (runs build.bat)
dotnet publish -p:PublishSingleFile=true -c Release -r win-x64 --self-contained true Key2Gif/Key2Gif.csproj
```

`build.bat` publishes and copies the exe to `E:\Apps` — it has **hardcoded absolute paths** and won't work elsewhere without editing. There is no test project and no CI.

## Architecture & Data Flow

```
App.xaml.cs (entry point, wires everything)
  ├── HotkeyService     → low-level keyboard hook, fires HotkeyPressed event
  ├── PickerWindow      → borderless WPF window (search + GIF grid)
  │     ├── GifService         → GIPHY REST API (search/trending)
  │     ├── RecentTracker      → LRU list of recently used GIFs, persisted JSON
  │     └── ClipboardInserter  → native Win32 clipboard + simulated Ctrl+V
  └── TaskbarIcon       → system tray (Hardcodet.NotifyIcon.Wpf)
```

**Startup flow** (`App.OnStartup`): Load `AppSettings` → if no API key, show `ApiKeyDialog` (blocks, exits app if cancelled) → create services → create hidden `PickerWindow` → register hotkey → create tray icon.

**Insert flow** (on GIF click in `PickerWindow`): track in `RecentTracker` → hide picker → `Task.Delay(200)` → `SetForegroundWindow(target)` → `Task.Delay(100)` → `ClipboardInserter.InsertGifAsync` (downloads GIF, puts on clipboard via native Win32, sends Ctrl+V) → restore clipboard after 500ms.

**Target window capture**: `GetForegroundWindow()` is called in `ShowPicker()` *before* the picker window is shown — this handle is used later to restore focus for paste insertion.

## Key Files & Patterns

- **`Services/HotkeyService.cs`** — Uses `WH_KEYBOARD_LL` (low-level keyboard hook), **not** `RegisterHotKey`. Tracks individual modifier key states (VK_LCONTROL, VK_LSHIFT/VK_RSHIFT, VK_RCONTROL). The hotkey is **LCtrl+Shift+RCtrl**.
- **`Services/ClipboardInserter.cs`** — Uses native Win32 clipboard (`OpenClipboard`/`SetClipboardData`), **not** WPF `Clipboard` class. Writes two formats: registered "GIF" format (preserves animation) and `CF_HDROP` (file drop). Falls back to `Clipboard.SetFileDropList` on failure. `SendCtrlV` uses `keybd_event` to simulate paste.
- **`Services/GifService.cs`** — GIPHY API client. Uses `System.Text.Json` document parsing (not strongly-typed deserialization). Pagination state (`_totalCount`, `_offset`) is instance-level and mutated on each call.
- **`Views/PickerWindow.xaml.cs`** — Heavy P/Invoke for window positioning. `PositionNearCaret()` has **5 fallback strategies**: GetGUIThreadInfo caret → focus rect → saved mouse pos → foreground window center → screen center. Search uses an 800ms `DispatcherTimer` debounce.
- **`Services/RecentTracker.cs`** — LRU pattern: removes duplicate by `FullUrl`, inserts at index 0, trims to max. `Changed` event triggers UI refresh. Persists to `%AppData%\Key2Gif\recent.json`.
- **`Services/Log.cs`** — File logger to `%AppData%\Key2Gif\key2gif.log`. All errors swallow exceptions internally (best-effort logging).

## Runtime State (all under `%AppData%\Key2Gif\`)

| File | Purpose |
|------|---------|
| `settings.json` | GIPHY API key (`GiphyApiKey`), `MaxRecentItems` |
| `recent.json` | Recently used GIFs (object with `gifs` array) |
| `key2gif.log` | Append-only log file |

## Gotchas

- **Hotkey is LCtrl+Shift+RCtrl**: The hotkey detection in `HotkeyService.cs` checks `VK_LCONTROL` + Shift + `VK_RCONTROL` specifically (left Ctrl + Shift + right Ctrl). The log message and tray tooltip correctly reference this.
- **Legacy/unused code**: `Services/EmojiDatabase.cs` and `Models/EmojiData.cs` contain a large hardcoded emoji database (~1000+ entries) that is **not referenced** anywhere in the active code path. This is leftover from when the app was an emoji picker. `RecentTracker.Load()` even has a comment about skipping "legacy format: just a list of emoji strings."
- **Known WPF crash suppression**: `App.xaml.cs:29-42` explicitly catches and swallows `ArgumentOutOfRangeException` — this is a known WPF Freezable crash from async bitmap downloads on detached visuals. Do not remove these handlers.
- **Picker auto-hides on deactivation**: `PickerWindow` has `Deactivated="OnDeactivated"` that calls `HidePicker()`. Any code that causes the window to lose focus will hide it.
- **Native clipboard timing is fragile**: `ClipboardInserter` relies on `Thread.Sleep` delays (80ms before paste, 5000ms before temp file cleanup). Changing these may break paste reliability.
- **`GifService` pagination is stateful**: `_offset` and `_totalCount` are mutated as side effects of `SearchAsync`/`GetTrendingAsync`. The caller (`PickerWindow`) reads `_gifService.Offset` after each call.
- **No DI container**: All services are manually instantiated in `App.xaml.cs` and passed by constructor. Microsoft.Extensions.Http is referenced but `GifService` creates its own `HttpClient` directly.

## NuGet Dependencies

- `Hardcodet.NotifyIcon.Wpf` — system tray icon
- `WpfAnimatedGif` — animated GIF playback in WPF Image controls
- `Microsoft.Extensions.Http` — referenced but not actively used for `HttpClient` management
