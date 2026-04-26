# TODO / Status

## Phases — done
- [x] **Phase 1** — Persistent settings + Serilog rolling logs.
- [x] **Phase 2** — UI-free `CopyEngine`, `ConflictPolicy`, per-item status icons.
- [x] **Phase 3a** — Add files/folder buttons, theme toggle, keep-awake, total progress + ETA.
- [x] **Phase 3b** — Toast + sound on completion, network throughput readout.
- [x] **Phase 3c** — Parallel small-file copies (default 4, threshold 10 MiB).
- [x] **Phase 3d** — Win32 `IFileOpenDialog` pickers (replaces WinRT pickers).
- [x] **Phase 4 (tests + CI)** — xUnit project (15 tests, all green), GitHub Actions workflow.

## Deferred / opt-in
- [ ] Full MVVM refactor (`MainViewModel`, `RelayCommand`, `IFileSystem` for `CopyEngine`). Invasive, no user-visible benefit.

## Resolved issues (kept for history)
- [Mitigated] Tool wouldn't launch via double-click — fixed by self-contained publish flags + Windows App SDK self-contained.
- [Mitigated] Drop area didn't show queued items / completion state — replaced with bound ListView and `ItemStatus` icons.
- [Mitigated] Progress bar didn't reflect per-file progress — fixed by reporting bytes from inside the copy loop.
- [Mitigated] Copy speed below disk/SMB ceiling — rewrote core: 1 MiB buffer (was 80 KiB), pipelined async I/O, `SequentialScan` hint, pre-allocate destination size, throttled progress to ~150 ms.
- [Fixed] Move mode deleting subdirectories twice — files deleted as they finish, empty source tree removed once at the top.
- [Fixed] WinRT `FolderPicker` "Select" grayed out / cross-MRU contamination — replaced with `IFileOpenDialog` + per-picker `ClientGuid`.
- [Fixed] Settings load wiped sound/network checkboxes — added `_suppressSettingsSave` guard during `ApplySettingsToUi`.
- [Fixed] `DestPathCombo` blanked when its `ItemsSource` was reassigned — bind once, mutate the `ObservableCollection` in place, re-set `Text` after mutation.
