# TODO / Status

## Phases — done
- [x] **Phase 1** — Persistent settings + Serilog rolling logs.
- [x] **Phase 2** — UI-free `CopyEngine`, `ConflictPolicy`, per-item status icons.
- [x] **Phase 3a** — Add files/folder buttons, theme toggle, keep-awake, total progress + ETA.
- [x] **Phase 3b** — Toast + sound on completion, network throughput readout.
- [x] **Phase 3c** — Parallel small-file copies (default 4, threshold 10 MiB).
- [x] **Phase 3d** — Win32 `IFileOpenDialog` pickers (replaces WinRT pickers).
- [x] **Phase 4 (tests + CI)** — xUnit project (15 tests, all green), GitHub Actions workflow.
- [x] **Phase 5 (launch + UI/JSON polish)** — `WindowsPackageType=None` bakes
      Bootstrap auto-init into plain `dotnet build` (fixes
      REGDB_E_CLASSNOTREG); MainWindow ScrollViewer + 720×560 MinSize +
      compact header/drop-hint/status row + F5/F6/Esc accelerators;
      `SettingsService` switched to System.Text.Json source-gen
      (`AppSettingsJsonContext`) — kills IL2026 trim warnings and speeds up
      load/save; defensive logging in `App.xaml.cs` startup catch and a
      build-then-swap `LoggingService.SetLevel`.

## Deferred / opt-in
- [ ] Full MVVM refactor (`MainViewModel`, `RelayCommand`, `IFileSystem` for `CopyEngine`). Invasive, no user-visible benefit.

## Resolved issues (kept for history)
- [Fixed] `dotnet build` output crashed with `REGDB_E_CLASSNOTREG` (0x80040154)
  from `DeploymentManager.AutoInitialize` — packaged-app build skipped the
  Bootstrapper. Fixed by setting `<WindowsPackageType>None</WindowsPackageType>`
  in the csproj so plain `dotnet build` produces an unpackaged .exe with
  Bootstrap auto-init wired up.
- [Fixed] Drop zone clipped + oversized title at low window resolutions —
  body wrapped in `ScrollViewer`, header switched to `SubtitleTextBlockStyle`,
  drop watermark 96→56, padding tightened, 720×560 minimum window size.
- [Fixed] IL2026 trim-analysis warnings on `JsonSerializer` calls under
  `PublishTrimmed=true` — `SettingsService` now uses a source-generated
  `AppSettingsJsonContext`.
- [Fixed] `LoggingService.SetLevel` could disable logging on transient failure
  — refactored to build new logger first, swap, then dispose old.
- [Fixed] `App.xaml.cs` startup catch silently swallowed init failures —
  now writes to `Debug.WriteLine` and best-effort `Log.Error`.
- [Mitigated] Tool wouldn't launch via double-click — fixed by self-contained publish flags + Windows App SDK self-contained.
- [Mitigated] Drop area didn't show queued items / completion state — replaced with bound ListView and `ItemStatus` icons.
- [Mitigated] Progress bar didn't reflect per-file progress — fixed by reporting bytes from inside the copy loop.
- [Mitigated] Copy speed below disk/SMB ceiling — rewrote core: 1 MiB buffer (was 80 KiB), pipelined async I/O, `SequentialScan` hint, pre-allocate destination size, throttled progress to ~150 ms.
- [Fixed] Move mode deleting subdirectories twice — files deleted as they finish, empty source tree removed once at the top.
- [Fixed] WinRT `FolderPicker` "Select" grayed out / cross-MRU contamination — replaced with `IFileOpenDialog` + per-picker `ClientGuid`.
- [Fixed] Settings load wiped sound/network checkboxes — added `_suppressSettingsSave` guard during `ApplySettingsToUi`.
- [Fixed] `DestPathCombo` blanked when its `ItemsSource` was reassigned — bind once, mutate the `ObservableCollection` in place, re-set `Text` after mutation.
