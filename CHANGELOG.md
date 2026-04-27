# Changelog

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
