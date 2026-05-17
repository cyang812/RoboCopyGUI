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

## Next version (planned) — Phase 6

See `ideas.md` for the full backlog. Items selected for the next release:

- [x] **Dynamic copy queue** — let the queue mutate at any time, including
      during an active copy.
  - Add a per-item "Remove" affordance (small ✕ button or right-click
    "Remove from queue") that's enabled only while the item's status is
    `Queued`.
  - Allow drag-drop and the Add files / Add folder buttons to keep working
    while a copy is running. New items append to the live queue and are
    picked up by the engine on the next iteration.
  - New `ItemStatus.Removed` for items the user removed before they
    started, so the engine skips them cleanly.
  - `CopyEngine.RunAsync` switches from a `foreach` snapshot to an indexed
    loop that re-reads `items.Count` and `items[i].Status` each iteration
    (with thread-safe access via `DispatcherQueue.TryEnqueue` or a lock).
    Preflight total bytes becomes a moving target; the engine should emit
    running-total updates instead of treating the total as fixed.
  - Tests: add `FakeCopyItem` scenarios for "add during run" and
    "remove pending mid-run".
  _Done: `CopyEngine` owns a live queue with explicit `AddItem` /
  `TryRemovePending` / `Snapshot` / `GetStatus` APIs. The engine tracks
  per-item state under its own lock (decoupled from the UI's async
  dispatch), uses an outer batched loop that picks up newly-added items
  on each iteration, computes a live denominator that grows/shrinks as
  items are added/removed, and reports `CopyTotals.Removed` separately.
  Each engine instance is one-shot (`InvalidOperationException` on
  re-use). MainWindow keeps Add/Drop enabled during copy and adds a
  per-row Remove (✕) button bound to `SourceItem.RemoveVisibility`.
  12 new tests cover add/remove flows, idempotency, snapshot ordering,
  live-denominator math, and the single-run guard._
- [ ] **Self-update checker** (idea #15) — query GitHub Releases API on
      launch; show an unobtrusive `InfoBar` if a newer version exists.
      Settings toggle to opt out.
- [ ] **Crash dump on unhandled exception** (idea #16) — hook
      `AppDomain.UnhandledException` and
      `TaskScheduler.UnobservedTaskException`, write a minidump via
      `MiniDumpWriteDump` P/Invoke to `crashes/<timestamp>.dmp` (capped
      at 5, LRU cleanup).
- [x] **`IFileSystem` abstraction in `CopyEngine`** (idea #19) — extract
      the file-system surface (Open / Create / Move / Delete / Exists /
      Enumerate / GetLastWriteTimeUtc / GetSize) behind an interface so
      the engine becomes mockable in tests. Required runway for several
      future features (verify-after-copy, resume, filters).
      _Done: `Services/IFileSystem.cs` + `RealFileSystem`; `CopyEngine`
      now takes an optional `IFileSystem` (defaults to `RealFileSystem.Instance`,
      so the WinUI call site is unchanged); test project ships an
      `InMemoryFileSystem` fake with 17 substitution + contract tests._

## Deferred / opt-in
- [ ] Full MVVM refactor (`MainViewModel`, `RelayCommand`). Invasive, no user-visible benefit.
  - Note: the `IFileSystem` half of this is now scheduled for Phase 6 (above) on its own merits.

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
