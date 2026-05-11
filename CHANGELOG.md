# Changelog

## Unreleased — Phase 5: launch reliability + UI/JSON polish

Follow-up pass after Phase 1–4 focused on three areas: making `dotnet build`
produce a directly-runnable .exe, tightening the main-window UI for low
resolutions, and removing reflection-based JSON serialization.

### Added — Build defaults: unpackaged by default
- `RoboCopyGUI.csproj` now sets `<WindowsPackageType>None</WindowsPackageType>`
  so a plain `dotnet build` (no flags) produces a runnable unpackaged .exe that
  uses the WindowsAppSDK Bootstrapper auto-init. Override with
  `-p:WindowsPackageType=Msix` for a packaged build.
- README "Build & run" snippet simplified accordingly.

### Added — UI polish (MainWindow)
- Body wrapped in a `ScrollViewer` (vertical Auto, horizontal Disabled) with an
  inner `MinHeight="420"` grid — fixes drop-zone clipping at low window heights
  by surfacing a scrollbar instead of silently truncating.
- Header restyled: "File Transfer Utility" now uses `SubtitleTextBlockStyle`
  (was the much larger `TitleTextBlockStyle` + bold), bottom margin trimmed
  20→12, body padding trimmed 30→20/12/16. Closes the dead band above the
  title that the screenshot review called out.
- Title bar height 36→32.
- Drop hint compacted: watermark icon 96→56 pt, removed the redundant
  "0 items selected" line, spacing 14→8. Drop hint now toggles `Visibility`
  (Visible/Collapsed) instead of dimming via `Opacity`, so it doesn't reserve
  vertical space once items are queued.
- Drop area got `MinHeight="160"` so it stays a usable target at low height.
- Status row collapsed from three rows to two: status text and overall
  "X / Y (NN%)" share a single 2-column row, and the per-file + overall
  ProgressBars are stacked tighter (4 px / 8 px heights).
- Keyboard accelerators on the action row: **F5** Start, **F6** Pause/Resume,
  **Esc** Cancel. Tooltips updated to surface the shortcut.
- Minimum window size enforced (720×560 raw pixels): WinUI 3 lacks
  `Window.MinWidth/MinHeight`, so `MainWindow` now subscribes `SizeChanged`
  and clamps via `AppWindow.Resize`. Also runs once at construction so
  freshly-launched windows can never start below the floor.

### Added — JSON serialization: source-generated, trim-safe
- `Services/SettingsService.cs` now uses a source-generated
  `AppSettingsJsonContext : JsonSerializerContext` (with
  `[JsonSourceGenerationOptions(WriteIndented=true, PropertyNamingPolicy=CamelCase)]`
  and `[JsonSerializable(typeof(AppSettings))]`) for both `Load` and `Save`.
  Removes the reflection-based `JsonSerializer.Serialize<T>/Deserialize<T>` calls
  and the IL2026 trim-analysis warnings they produced under
  `PublishTrimmed=true`. Faster startup serialization as a bonus.

### Fixed
- **Crash on launch via `dotnet build` output (`REGDB_E_CLASSNOTREG` /
  `0x80040154` from `DeploymentManager.AutoInitialize`)** — root cause was that
  `dotnet build` (without `-p:WindowsPackageType=None`) produced a packaged-app
  layout that disables Bootstrap auto-init, so `DeploymentInitializeOptions`
  couldn't be activated. Fix: bake `WindowsPackageType=None` into the csproj
  (see "Build defaults" above).
- **`App.xaml.cs` startup catch silently swallowed init failures** — now logs
  to `Debug.WriteLine` and best-effort `Log.Error` while still preventing the
  app from crashing on logging/settings/notifications init.
- **`LoggingService.SetLevel` could leave the app loggerless** — previously it
  closed the existing logger before building the replacement, so any failure
  during construction left `Log.Logger` pointing at a disposed sink. Refactored
  to build new → swap → dispose old, with the swap guarded so a `BuildLogger`
  failure can't lose the prior sink. New shared `BuildLogger(LogEventLevel)`
  helper used by both `Initialize` and `SetLevel`.

### Notes
- Validated end-to-end: `dotnet build` is clean (0 warnings, 0 errors — the
  prior IL2026 warnings are gone after source-gen), `dotnet test` is 15/15
  green, and the unpackaged .exe launches directly with the Windows App
  Runtime 1.8 installed.
- Full MVVM refactor remains intentionally deferred (see TODO.md).

## Unreleased — Phase 1–4

This branch turns the original prototype into a production-quality WinUI 3 file copier with persistence, structured logging, a UI-free copy engine, parallel small-file copies, unit tests, and CI.

### Added — Phase 1: Settings & logging
- `Services/AppSettings.cs` + `Services/SettingsService.cs` — JSON settings persisted next to the exe (portable). Stores destination, destination history (capped at 12), conflict policy, parallelism, threshold, theme, notification preferences, last source folder, log level.
- `Services/LoggingService.cs` — Serilog 4.2 with rolling daily file sink (7-day retention) under `<exe>/logs` plus debug sink. Bootstrapped from `App.xaml.cs`.

### Added — Phase 2: Copy engine
- `Services/CopyEngine.cs` — UI-free async copy engine with `ICopyItem` interface. Pipelined async I/O (1 MiB buffer, `Asynchronous | SequentialScan`, pre-allocation), mtime preservation, per-file try/catch, EMA-based ETA, `Preflight` for total-byte computation.
- `Services/Enums.cs` — `ItemStatus`, `ConflictPolicy` (Overwrite / Skip / SkipIfSame / Rename).
- `Services/PauseTokenSource.cs` — cooperative pause primitive.
- `MainWindow.xaml(.cs)` — per-item status icons in the drop list; conflict policy combo bound to settings.

### Added — Phase 3a: UX polish
- Add files… / Add folder… / Clear list buttons.
- Light/dark theme toggle (sun/moon `FontIcon` swapped via `Services/ThemeService.cs`).
- Total progress bar with ETA on top of per-file progress.
- Keep-awake during copy (`Services/PowerService.cs` → `SetThreadExecutionState`).

### Added — Phase 3b: Notifications & throughput
- `Services/NotificationService.cs` — toast on completion (success or failure), optional system sound.
- `Services/NetworkMonitor.cs` — samples bytes/sec on the active interface and surfaces it in the status bar.

### Added — Phase 3c: Parallel small-file copies
- `CopyOptions.SmallFileThresholdBytes` (default 10 MiB) and `MaxParallelSmallFiles` (default 4).
- `CopyEngine` splits each directory's files into a small bucket (Phase A: `Parallel.ForEachAsync`, completion-only progress) and a large bucket (Phase B: sequential pipelined copy with per-buffer progress).
- `Interlocked.Add` for shared byte counter; report serialized via lock.
- UI: NumberBox for parallelism (1 = sequential).

### Added — Phase 3d: Native pickers (Win32)
- `Services/Win32Dialogs.cs` — `IFileOpenDialog` wrappers replacing WinRT `FolderPicker` / `FileOpenPicker`.
- Distinct `ClientGuid` per picker (destination / source-folder / source-files) so the system MRU doesn't cross-contaminate.
- `prefillLeafForReselect` mode: opens at the parent and `SetFileName(leaf)` so first-click confirms the previously-chosen destination.
- `TrySetFolder` with UNC-aware `SHCreateItemFromParsingName` and ancestor-walk fallback.

### Added — Phase 4: Tests + CI
- `Tests/RoboCopyGUI.Tests.csproj` — plain `net8.0` xUnit project (no WinUI deps). Source-links `CopyEngine.cs`, `PauseTokenSource.cs`, `Enums.cs` rather than referencing the WinUI csproj.
- `Tests/TestHelpers.cs` — `FakeCopyItem` (records status transitions) and `TempDir` (auto-cleaned scratch directory).
- `Tests/CopyEngineTests.cs` — 12 tests: single file, recursive directory, move-mode delete (file + tree), all four conflict policies, per-file resilience, pre-cancellation, `Preflight` totalling, parallel small-file copies (24 × 4 KiB, parallelism 4).
- `Tests/PauseTokenSourceTests.cs` — 3 tests: pause/resume releases waiters, cancel during pause throws, no-op when not paused.
- `.github/workflows/ci.yml` — `test` job on `ubuntu-latest`, `build-and-publish` matrix on `windows-latest` for `win-x64` and `win-arm64`, artifacts uploaded.
- `RoboCopyGUI.csproj` — explicit `Compile/EmbeddedResource/None/Page/ApplicationDefinition Remove="Tests\**"` so the WinUI app's default globs don't pick up test files.

### Fixed
- Settings load race that wiped `PlaySoundOnCompletion` / `ShowNetworkThroughput` checkboxes (introduced `_suppressSettingsSave` flag in `ApplySettingsToUi`).
- DestPathCombo blanking during copy or history mutations — bind once and mutate the bound `ObservableCollection` in place via Insert/Move/RemoveAt; re-set `Text` after mutations.
- WinRT `FolderPicker` "Select" button grayed out / cross-picker MRU contamination — replaced with `IFileOpenDialog` (see Phase 3d).
- Move mode double-deleting subdirectories — files now deleted as they complete; empty source tree removed once at the top.
- Copy throughput on SMB / HDD — 1 MiB buffer, pipelined async I/O, pre-allocation, throttled progress reporting (~150 ms).

### Notes
- `PublishSingleFile=true` is unsupported with WinUI 3 unpackaged — crashes `0xc000027b` on `Microsoft.UI.Xaml.dll`. Ship the `publish/` folder.
- Full MVVM refactor (Phase 4 part 3) is intentionally deferred — invasive with no user-visible benefit.
