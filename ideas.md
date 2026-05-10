# Ideas backlog

Brainstorm captured 2026-05-10. Items are not commitments — they're a menu to
draw from when planning future versions. Each idea has a one-line "why" and a
sketch of effort / approach.

The legend after each title:
- 🔥 high impact
- ⚡ low/medium effort
- 🧩 invasive / engine-level
- 🎨 UI-only

Items already promoted to the active TODO are tagged **(scheduled)** with a
pointer to `TODO.md`.

---

## A. Killer features

### 1. Verify-after-copy (hash check) 🔥🧩
**Why** — High-confidence transfers over SMB / flaky USB / large archives.
The single biggest credibility upgrade for a "serious" copy tool.

**Sketch** — Add `VerifyMode` (`None` / `Size` / `Hash`) to `CopyOptions`.
After each file copy, re-read source + dest in parallel and `xxHash64`-compare
(System.IO.Hashing). New `ItemStatus.Verifying`. Surface a "Verify" toggle
in the UI. ~200 LOC + tests.

### 2. Resume interrupted copies 🔥🧩
**Why** — Cancel / crash / network drop today loses the in-flight file.
Resume = trust on huge files.

**Sketch** — On cancel/error, persist `(srcPath, destPath, bytesWritten,
mtime, size)` to a sidecar `.copyresume` JSON next to the destination root.
On next launch, detect and offer "Resume previous transfer". CopyEngine
gains a `Stream.Seek` start-offset path. ~150 LOC.

### 3. ReFS block-clone for same-volume copies 🔥
**Why** — Instant copy of multi-GB files when source + destination are on the
same ReFS volume. Robocopy doesn't even do this.

**Sketch** — Detect via `GetVolumeInformation` that filesystem == ReFS and
src/dst share the same volume. Use
`DeviceIoControl(FSCTL_DUPLICATE_EXTENTS_TO_FILE)` to clone extents. Fall
back to normal copy on mismatch. Genuinely magical UX on dev machines.

### 4. Bandwidth throttling 🔥⚡
**Why** — Crucial when copying over the office VPN / shared SMB so you don't
saturate the link.

**Sketch** — Token-bucket on the read loop in `CopyEngine`. UI: `NumberBox`
"Limit: __ MB/s (0 = unlimited)". Persist in settings. ~80 LOC.

### 5. Filters: include/exclude masks + size/date 🔥⚡
**Why** — "Copy this folder but skip `*.tmp` and files >2 GB" is a top-3
robocopy use case.

**Sketch** — `CopyOptions.IncludeGlobs / ExcludeGlobs / MinSize / MaxSize /
MinDate / MaxDate`. UI: small "Filter…" flyout. Use
`Microsoft.Extensions.FileSystemGlobbing` (already a transitive ref via SDK).
~150 LOC + tests.

---

## B. UI / UX

### 6. Mica / Acrylic backdrop 🎨⚡
**Why** — Native Windows 11 feel; current window is flat.

**Sketch** — Set `SystemBackdrop="MicaBackdrop"` on the Window with a
fallback shim for older Windows. ~10 LOC.

### 7. Per-item right-click menu 🎨⚡
**Why** — Today the only way to remove a queued item is "Clear list". Add:
Remove / Open in Explorer / Copy path / Skip.

**Sketch** — `MenuFlyout` on the ListView item with conditional
`IsEnabled` based on `ItemStatus`. ~50 LOC.

> **Superseded by the scheduled "dynamic copy queue" item — see TODO.md.**
> The remove half of #7 is folded into that work.

### 8. System tray icon + minimize-to-tray during copy 🎨
**Why** — "Fire and forget" big copies without a window taking up the
taskbar.

**Sketch** — `H.NotifyIcon.WinUI` NuGet (or P/Invoke `Shell_NotifyIcon`)
plus a checkbox "Minimize to tray when copy starts". Tray menu: Show /
Pause / Cancel / Quit. ~120 LOC.

### 9. Localization (zh-CN + en-US) 🎨
**Why** — User locale is zh-CN. Both audiences benefit; tedious but
mechanical.

**Sketch** — `Strings/en-US/Resources.resw` + `zh-CN/Resources.resw`,
`x:Uid` on every text element, language picker in settings.

### 10. Compact "transfer" mode 🎨⚡
**Why** — After Start, collapse to a small ~400×120 window with just
progress + ETA + cancel. Ideal for long copies.

**Sketch** — Visual state group; bind controls' `Visibility`. ~80 LOC.

### 11. Drag-out to Explorer 🎨⚡
**Why** — Drag a completed item from the queue back out to a different
Explorer window.

**Sketch** — Implement `DataPackage` with `StorageItems` for a
`DragItemsStarting` handler. ~60 LOC.

### 12. Better empty-state illustration + tip rotator 🎨⚡
**Why** — Current is just a glyph. Show a 2-line tip cycling between
"F5 Start · F6 Pause · Esc Cancel", "Drag a folder anywhere", etc.

**Sketch** — Pure XAML + a `DispatcherTimer`. ~30 LOC.

---

## C. Performance / reliability

### 13. Disk-type-aware parallelism 🔥
**Why** — SSD → 8, HDD → 1, network → 4. Today user has to guess.

**Sketch** — Probe via `StorageNode.IsRotational` or
`DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY)`. Run once per source/dest at
the start of each run. Override allowed in settings. ~80 LOC.

### 14. Retry-with-backoff for transient errors ⚡
**Why** — `ERROR_SHARING_VIOLATION`, `ERROR_NETWORK_BUSY`, AV scanner
glitches all benefit. Currently any error → file marked Failed.

**Sketch** — Wrap the copy step in a Polly-style retry (3 attempts, 250 ms /
1 s / 4 s with jitter). Only retry on the known-transient HRESULTs.
~50 LOC + tests.

### 15. Self-update checker ⚡ **(scheduled)**
**Why** — Query GitHub Releases API once per launch; show "Update
available" inline. Free distribution channel.

**Sketch** — `GET /repos/cyang812/RoboCopyGUI/releases/latest`, compare
semver with `AssemblyInformationalVersion`. Show an unobtrusive `InfoBar`
in the title bar. Opt-out toggle. ~100 LOC.

### 16. Crash dump on unhandled exception ⚡ **(scheduled)**
**Why** — Massively helps debug user-reported issues.
`AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`
write a minidump + log to `crashes/<timestamp>.dmp`.

**Sketch** — `MiniDumpWriteDump` P/Invoke. Always-on. Capped at 5 dumps,
LRU cleanup. ~80 LOC.

### 17. Disk-space precheck ⚡
**Why** — Already deferred from earlier phases. Compute `Preflight` total,
compare against `DriveInfo.AvailableFreeSpace`, warn before starting.

**Sketch** — Pre-Start check in `MainWindow.OnStartClicked`; show a
`ContentDialog` if short. ~30 LOC.

---

## D. Code quality / engineering

### 18. Split `MainWindow.xaml.cs` (840 lines) into partials ⚡
**Why** — Without going full MVVM. One partial per concern: theme, picker,
copy orchestration, drag-drop, network, settings sync. Pure refactor.

**Sketch** — `MainWindow.Theme.cs`, `MainWindow.Pickers.cs`,
`MainWindow.Copy.cs`, etc. Keep `MainWindow.xaml.cs` thin (constructor +
event hookups). Big readability win, zero behavior change.

### 19. `IFileSystem` abstraction in `CopyEngine` 🧩 **(scheduled)**
**Why** — Lets us mock disk in tests instead of touching real disk. Test
count grows quickly; `CopyEngine` becomes platform-agnostic.

**Sketch** — Define `IFileSystem` (Open, Create, Move, Delete, Exists,
Enumerate, GetLastWriteTimeUtc, GetSize). Default `RealFileSystem` wraps
`System.IO`. Tests inject an in-memory implementation. ~150 LOC + many
test wins (and required for some upcoming features like #1, #2, #5).

### 20. Replace SizeChanged-clamp with WM_GETMINMAXINFO subclass ⚡
**Why** — Smoother min-size enforcement than the current "snap-back"
during a drag-resize.

**Sketch** — `SetWindowSubclass` P/Invoke; intercept
`WM_GETMINMAXINFO` and set `MINMAXINFO.ptMinTrackSize`. ~60 LOC.

### 21. `CommunityToolkit.Mvvm` for `SourceItem` ⚡
**Why** — Even without full MVVM, `[ObservableProperty]` on `SourceItem`
fields kills boilerplate.

**Sketch** — Add NuGet, mark fields, regenerate. ~30 LOC delta.

---

## E. DevOps / distribution

### 22. Code-signing the published exe
**Why** — SmartScreen warns on unsigned downloads. Friction for new users.

**Sketch** — CI step + `signtool.exe`. Azure Trusted Signing has a free
tier; or self-signed for now and EV cert later.

### 23. WinGet manifest
**Why** — `winget install RoboCopyGUI`. Free distribution channel.

**Sketch** — Submit YAML to `microsoft/winget-pkgs` once a release exists.

### 24. GitHub Releases on tag push ⚡
**Why** — CI today produces artifacts but doesn't release them.

**Sketch** — Add a `release` job triggered on `v*` tag pushes; uses
`softprops/action-gh-release` to attach the matrix artifacts. ~20 lines
in `ci.yml`.

### 25. Dependabot + CodeQL ⚡
**Why** — Free, pure config files. Standard hygiene.

**Sketch** — `.github/dependabot.yml` (nuget + actions) and
`.github/workflows/codeql.yml`. ~30 LOC total.

### 26. README screenshots / demo GIF ⚡
**Why** — First-impression killer for any tool.

**Sketch** — ScreenToGif → MP4 + still PNGs in `docs/`. README embeds.

### 27. LICENSE file ⚡
**Why** — Repo has no LICENSE — currently "all rights reserved" which
discourages contributions.

**Sketch** — One file, MIT or Apache-2.0.

---

## F. Power-user / extensibility

### 28. CLI mode 🧩
**Why** — Scriptable from PowerShell. Opens "RoboCopyGUI as a robocopy.exe
replacement" door.

**Sketch** — Parse `args` in `App()`; if any non-empty, skip window and
run a console-mode copy via the same `CopyEngine`. Flags: `--src`,
`--dst`, `--policy`, `--parallel`, `--verify`, `--quiet`. ~150 LOC.

### 29. Explorer context-menu integration
**Why** — Right-click folder → "Copy to… (RoboCopyGUI)".

**Sketch** — Either a `SendTo` shortcut (trivial, install-time) or a real
shell extension (significant, packaged-app territory). Start with the
`SendTo` variant.

### 30. Copy "presets" ⚡
**Why** — Save (name + source + destination + policy) combos for repeated
transfers (e.g., "Backup project to NAS").

**Sketch** — Extend `AppSettings` with `List<CopyPreset>`. UI flyout
"Save current as preset" / "Load preset". ~100 LOC.
