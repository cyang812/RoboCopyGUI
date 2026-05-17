<p align="center">
  <img src="docs/screenshots/appicon.png" alt="RoboCopyGUI logo" width="128" height="128">
</p>

<h1 align="center">RoboCopyGUI</h1>

<p align="center">
  A modern, fast Windows file copy / move utility built with
  <strong>C# / .NET 8 / WinUI 3</strong> and the
  <strong>Windows App SDK</strong>.
  <br>Designed for large transfers over SMB and slow disks, with a Fluent UI,
  pause / resume / cancel, conflict policies, a dynamic queue, structured
  logging, and persistent settings.
</p>

<p align="center">
  <a href="https://github.com/cyang812/RoboCopyGUI/actions/workflows/ci.yml"><img src="https://github.com/cyang812/RoboCopyGUI/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="https://github.com/cyang812/RoboCopyGUI/actions/workflows/codeql.yml"><img src="https://github.com/cyang812/RoboCopyGUI/actions/workflows/codeql.yml/badge.svg?branch=main" alt="CodeQL"></a>
  <a href="https://github.com/cyang812/RoboCopyGUI/releases/latest"><img src="https://img.shields.io/github/v/release/cyang812/RoboCopyGUI?include_prereleases&sort=semver" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/WinUI-3-0078D4?logo=windows" alt="WinUI 3">
</p>

---

## Features

### UI / UX
- Fluent / WinUI 3 unpackaged app, custom title bar, light/dark theme toggle (sun/moon icon).
- Drag-and-drop drop zone with large dim watermark and per-item status icons.
- **Dynamic queue** — add new files/folders or remove still-pending items at any time, **including during an active copy**. Each row has a small ✕ Remove button while it's Queued.
- Editable destination combo box with **destination history** (recent destinations dropdown).
- Add files / Add folder / Clear list buttons (no need to drag).
- Live status: per-file progress bar, total progress bar with **ETA**, network throughput readout (MB/s).
- Toast notification + optional sound on completion.
- Keep-awake during long copies (suppresses sleep / display-off).
- **Self-update InfoBar** on launch when a newer GitHub release exists. Opt-out via a settings checkbox.
- Custom app icon embedded in the .exe so taskbar / Alt-Tab / Explorer show the RoboCopyGUI tile instead of the generic .NET icon.

### Copy engine
- Pipelined async I/O (1 MiB buffer, `FileOptions.Asynchronous | SequentialScan`, pre-allocated destination size) — tuned for SMB and HDDs.
- **Parallel small-file copies** (configurable, default 4) with a configurable threshold (default 10 MiB). Files ≥ threshold are copied sequentially with full per-buffer progress reporting; files below run in parallel for throughput.
- **Conflict policies**: Overwrite / Skip / Skip if same (size + mtime) / Rename (`name (1).ext`).
- **Move mode** (`Delete source after copy`) — files deleted as they complete; empty source tree removed once at the top.
- mtime preservation, per-file try/catch so one failure doesn't abort siblings.
- **Pause / Resume / Cancel** via `PauseTokenSource` and `CancellationTokenSource`.
- Engine is UI-independent and routes all FS access through an `IFileSystem` abstraction (with a default `RealFileSystem` and a test-only `InMemoryFileSystem` fake). Unit-testable end-to-end without touching real disk.

### Persistence & diagnostics
- Settings persisted as JSON next to the exe (portable). Includes destination history, conflict policy, parallelism, theme, notification preferences, last-used source folder, update-check opt-out.
- Structured logging via **Serilog** (daily rolling, 7-day retention) under `<exe>/logs`.
- **Crash dumps** — unhandled exceptions write a native minidump + a readable `.txt` sidecar to `<exe>/crashes/`, capped at 5 dumps (LRU). Attaching one file is enough for a bug report.

### Native pickers
- Replaced WinRT `FolderPicker` / `FileOpenPicker` with **Win32 `IFileOpenDialog`** — fixes the "Select" grayed-out bug, the cross-MRU contamination between source/dest pickers, and the "interpret leaf as child" bug when re-opening a previously chosen folder.

## Screenshots

> Placeholders — see [`docs/screenshots/HOW_TO_CAPTURE.md`](docs/screenshots/HOW_TO_CAPTURE.md)
> for the recipe. Drop a PNG with the matching filename into
> `docs/screenshots/` and the links below start rendering automatically.

<p align="center">
  <img src="docs/screenshots/mainwindow-light.png" alt="Main window (Light theme)" width="48%">
  <img src="docs/screenshots/mainwindow-dark.png"  alt="Main window (Dark theme)"  width="48%">
</p>

## Project layout

```
RoboCopyGUI.csproj          WinUI 3 app (net8.0-windows10.0.19041.0)
App.xaml(.cs)               App entrypoint, Serilog bootstrap, crash-dump install
MainWindow.xaml(.cs)        Main window, drag/drop, progress, settings UI
Services/
  CopyEngine.cs             UI-free async copy engine + ICopyItem + live queue
  IFileSystem.cs            FS abstraction + RealFileSystem default
  PauseTokenSource.cs       Cooperative pause primitive
  Enums.cs                  ItemStatus (incl. Removed), ConflictPolicy
  AppSettings.cs            JSON-serialized user settings (System.Text.Json source-gen)
  SettingsService.cs        Load/save settings
  LoggingService.cs         Serilog config
  NetworkMonitor.cs         Throughput sampling
  NotificationService.cs    Toast + sound on completion
  PowerService.cs           Keep-awake (SetThreadExecutionState)
  ThemeService.cs           Light/dark toggle
  Win32Dialogs.cs           IFileOpenDialog wrappers
  CrashDumpService.cs       AppDomain / TaskScheduler / WinUI unhandled-exception hooks → minidump
  SelfUpdateService.cs      GitHub Releases check + semver compare
Tests/
  RoboCopyGUI.Tests.csproj  xUnit (net8.0, no WinUI deps)
  CopyEngineTests.cs                 15 real-disk tests (TempDir)
  CopyEngineInMemoryTests.cs         17 in-memory FS substitution + contract tests
  CopyEngineDynamicQueueTests.cs     12 add/remove/snapshot/live-denominator tests
  CrashDumpServiceTests.cs           10 filename + LRU + end-to-end sidecar tests
  SelfUpdateServiceTests.cs          29 semver/JSON/HTTP tests via fake HttpMessageHandler
  PauseTokenSourceTests.cs           3 pause/resume/cancel tests
  InMemoryFileSystem.cs              Single-lock fake IFileSystem
  TestHelpers.cs                     FakeCopyItem (with OnStatus hook) + TempDir
Assets/
  AppIcon.ico               Embedded into the .exe via <ApplicationIcon>
docs/
  ARCHITECTURE.md           Comprehensive technical guide (~400 lines)
  screenshots/              README hero + manual capture spot
.github/
  workflows/ci.yml          Test + build + publish (win-x64, win-arm64); release-on-tag
  workflows/codeql.yml      CodeQL security analysis (push/PR + weekly)
  dependabot.yml            Weekly NuGet + Actions updates (grouped)
tools/
  make-icon.py              Pillow-based generator for Assets/AppIcon.ico
```

## Build & run

Requires Windows 10 19041+ and the .NET 8 SDK.

```pwsh
# Debug build (csproj defaults to WindowsPackageType=None so the .exe is
# directly runnable; pass -p:WindowsPackageType=Msix to produce an MSIX build).
dotnet build

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

**83 tests** across six suites (see Project layout above). The test project
targets plain `net8.0` (no WinUI deps), so it runs on Linux CI agents.

## How to use

1. Launch the app.
2. Drag files/folders into the dashed drop area, or click **Add files…** / **Add folder…**. (Both work mid-copy too — new items append to the live queue.)
3. Pick a destination from the dropdown, type one in, or click the folder button.
4. Choose a conflict policy and (optionally) tick **Delete source after copy** for move mode.
5. Click **Start**. Use **Pause** / **Cancel** as needed, or click the ✕ on a queued item to drop it before the engine reaches it.

## Releasing a new version

CI auto-publishes a GitHub Release whenever you push a `v*` tag — the
in-app self-update InfoBar picks it up on the next launch.

```pwsh
# from main, after merging your changes
git tag v1.2.3
git push origin v1.2.3
```

The `release` job in `ci.yml` runs after the matrix build, downloads each
RID's publish folder, zips them as `RoboCopyGUI-v1.2.3-win-x64.zip` /
`-win-arm64.zip`, and attaches both to a new release with auto-generated
notes. Pre-release tags (e.g. `v1.2.3-rc1`) are marked `prerelease=true`
so the in-app check (which only inspects `/releases/latest`) ignores them.

## Architecture

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for a comprehensive
walkthrough of the layered design, the copy pipeline, pause/cancel/progress
contracts, and the WinUI-specific gotchas.

