# RoboCopyGUI

A modern, fast Windows file copy/move utility built with **C# / .NET 8 / WinUI 3** and the **Windows App SDK**. Designed for large transfers over SMB and slow disks, with a Fluent UI, pause/resume/cancel, conflict policies, structured logging, and persistent settings.

## Features

### UI / UX
- Fluent / WinUI 3 unpackaged app, custom title bar, light/dark theme toggle (sun/moon icon).
- Drag-and-drop drop zone with large dim watermark and per-item status icons.
- Editable destination combo box with **destination history** (recent destinations dropdown).
- Add files / Add folder / Clear list buttons (no need to drag).
- Live status: per-file progress bar, total progress bar with **ETA**, network throughput readout (MB/s).
- Toast notification + optional sound on completion.
- Keep-awake during long copies (suppresses sleep / display-off).

### Copy engine
- Pipelined async I/O (1 MiB buffer, `FileOptions.Asynchronous | SequentialScan`, pre-allocated destination size) — tuned for SMB and HDDs.
- **Parallel small-file copies** (configurable, default 4) with a configurable threshold (default 10 MiB). Files >= threshold are copied sequentially with full per-buffer progress reporting; files below run in parallel for throughput.
- **Conflict policies**: Overwrite / Skip / Skip if same (size + mtime) / Rename (`name (1).ext`).
- **Move mode** (`Delete source after copy`) — files deleted as they complete; empty source tree removed once at the top.
- mtime preservation, per-file try/catch so one failure doesn't abort siblings.
- **Pause / Resume / Cancel** via `PauseTokenSource` and `CancellationTokenSource`.

### Persistence & diagnostics
- Settings persisted as JSON next to the exe (portable). Includes destination history, conflict policy, parallelism, theme, notification preferences, last-used source folder.
- Structured logging via **Serilog** (daily rolling, 7-day retention) under `<exe>/logs`.

### Native pickers
- Replaced WinRT `FolderPicker` / `FileOpenPicker` with **Win32 `IFileOpenDialog`** — fixes the "Select" grayed-out bug, the cross-MRU contamination between source/dest pickers, and the "interpret leaf as child" bug when re-opening a previously chosen folder.

## Project layout

```
RoboCopyGUI.csproj          WinUI 3 app (net8.0-windows10.0.19041.0)
App.xaml(.cs)               App entrypoint, Serilog bootstrap
MainWindow.xaml(.cs)        Main window, drag/drop, progress, settings UI
Services/
  CopyEngine.cs             UI-free async copy engine + ICopyItem
  PauseTokenSource.cs       Cooperative pause primitive
  Enums.cs                  ItemStatus, ConflictPolicy, ...
  AppSettings.cs            JSON-serialized user settings
  SettingsService.cs        Load/save settings
  LoggingService.cs         Serilog config
  NetworkMonitor.cs         Throughput sampling
  NotificationService.cs    Toast + sound on completion
  PowerService.cs           Keep-awake (SetThreadExecutionState)
  ThemeService.cs           Light/dark toggle
  Win32Dialogs.cs           IFileOpenDialog wrappers
Tests/
  RoboCopyGUI.Tests.csproj  xUnit (net8.0, no WinUI deps)
  CopyEngineTests.cs        12 tests: copy, recurse, move, conflict policies, parallelism, cancellation, preflight
  PauseTokenSourceTests.cs  3 tests: pause/resume/cancel-during-pause
.github/workflows/ci.yml    Build + test + publish (win-x64, win-arm64) on push/PR to main
```

## Build & run

Requires Windows 10 19041+ and the .NET 8 SDK.

```pwsh
# Debug build
dotnet build RoboCopyGUI.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None

# Self-contained publish (this is what to ship — single click to run)
dotnet publish RoboCopyGUI.csproj -c Release -r win-x64 -p:Platform=x64 `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:SelfContained=true `
  -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false `
  -p:EnableMsixTooling=true
# -> bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\RoboCopyGUI.exe
```

> `PublishSingleFile=true` is **not** supported — it crashes with `0xc000027b` on `Microsoft.UI.Xaml.dll`. Ship the `publish/` folder instead.

To trim the published size, override the kept locale list:
```pwsh
dotnet publish ... -p:KeepLocales="en-us;zh-cn"
```

## Tests

```pwsh
dotnet test Tests\RoboCopyGUI.Tests.csproj
```
15 tests covering the copy engine and pause primitive. The test project targets plain `net8.0` (no WinUI deps), so it runs on Linux CI agents.

## How to use

1. Launch the app.
2. Drag files/folders into the dashed drop area, or click **Add files…** / **Add folder…**.
3. Pick a destination from the dropdown, type one in, or click the folder button.
4. Choose a conflict policy and (optionally) tick **Delete source after copy** for move mode.
5. Click **Start**. Use **Pause** / **Cancel** as needed.
