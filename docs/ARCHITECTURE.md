# RoboCopyGUI Architecture & Technical Guide

Audience: strong C# developer with some WinForms/WPF exposure, new to WinUI 3.

Goal: code-level understanding without going line-by-line. After reading this, you should know where to look for any change, and which files matter.

---

## 1. WinUI 3 Mental Model (vs. WinForms/WPF)

If you know WPF, WinUI 3 is WPF-like XAML running on top of Win32 + Windows App SDK.
If you know WinForms, this is much closer to WPF than WinForms.

| Concept | WinForms | WPF | WinUI 3 |
|---|---|---|---|
| UI definition | Designer + `.Designer.cs` | XAML + code-behind | XAML + code-behind |
| Markup language | N/A | XAML | XAML (very similar to WPF) |
| Rendering | GDI+/USER32 | DirectX (WPF runtime) | DirectComposition / Win2D / Visual Layer |
| Element type | `Control` | `FrameworkElement` | `FrameworkElement` (same idea) |
| Layout | Anchor/Dock | Panels (`Grid`, `StackPanel`) | Panels (`Grid`, `StackPanel`) |
| Lookless controls | No | Yes (`ControlTemplate`) | Yes |
| Data binding | Manual | `{Binding Path=...}` | `{Binding}` and `{x:Bind}` |
| Resource lookup | N/A | `{StaticResource}` / `{ThemeResource}` | Same, plus theme-aware `ThemeResource` |
| Threading | UI thread | `Dispatcher` | `DispatcherQueue` |

### Things Specific to WinUI 3 That Matter Here

1. It looks like WPF but is not WPF. Namespaces are `Microsoft.UI.Xaml.*`, not `System.Windows.*`. APIs differ (for example, no `Window.MinWidth`, no `Window.Topmost`, no `MessageBox.Show`).
2. You use two window APIs: `Window` and `AppWindow`. Some features exist on only one of them.
3. Packaged vs. unpackaged is a real fork. This project ships unpackaged (`<WindowsPackageType>None</WindowsPackageType>`), which is required for the startup path we use.
4. UI thread/STA still matters for WinRT calls. `DispatcherQueue.TryEnqueue(...)` is the `Invoke/BeginInvoke` equivalent.
5. No `App.config` / `Settings.Default`; settings are custom JSON near the executable.
6. No `MessageBox`; WinUI-native pattern is `ContentDialog`.
7. XAML compiler errors can surface as misleading C# build errors.

---

## 2. Project Layout

```text
RoboCopyGUI/
├── App.xaml(.cs)                  # App entry point; owns settings/logging
├── MainWindow.xaml(.cs)           # Main window and orchestration
├── app.manifest
├── Package.appxmanifest
├── RoboCopyGUI.csproj
├── RoboCopyGUI.slnx
├── Assets/
├── Properties/
│   ├── launchSettings.json
│   └── PublishProfiles/
├── Services/
│   ├── AppSettings.cs
│   ├── CopyEngine.cs
│   ├── Enums.cs
│   ├── LoggingService.cs
│   ├── NetworkMonitor.cs
│   ├── NotificationService.cs
│   ├── PauseTokenSource.cs
│   ├── PowerService.cs
│   ├── SettingsService.cs
│   ├── ThemeService.cs
│   └── Win32Dialogs.cs
├── Tests/
├── docs/
├── .github/workflows/ci.yml
├── CHANGELOG.md
├── README.md
├── Requirement.md
├── TODO.md
└── ideas.md
```

### Core Split

- App + main window = WinUI shell (UI thread, XAML, event handlers)
- `Services/` = engine and infrastructure (mostly pure C#)

Key entry files:

- [App.xaml.cs](../App.xaml.cs)
- [MainWindow.xaml](../MainWindow.xaml)
- [MainWindow.xaml.cs](../MainWindow.xaml.cs)
- [Services/CopyEngine.cs](../Services/CopyEngine.cs)

---

## 3. Application Startup Flow

```text
.exe launched
  -> WindowsAppSDK bootstrapper auto-init (WindowsPackageType=None)
  -> App() constructor (App.xaml.cs)
      -> SettingsService.Load()
      -> LoggingService.Initialize(...)
      -> NotificationService.Initialize()
      -> InitializeComponent()
  -> App.OnLaunched(...)
      -> new MainWindow()
      -> subscribe Closed handler
      -> Activate()
  -> MainWindow ctor (MainWindow.xaml.cs)
      -> InitializeComponent()
      -> capture DispatcherQueue
      -> custom title bar setup
      -> apply settings + theme
      -> enforce minimum size
```

Two deliberate app-wide globals in [App.xaml.cs](../App.xaml.cs):

```csharp
public static AppSettings Settings { get; private set; }
public static DispatcherQueue? UiDispatcher { get; internal set; }
```

---

## 4. Main Window Responsibilities

Main UI and orchestration are primarily in:

- [MainWindow.xaml](../MainWindow.xaml)
- [MainWindow.xaml.cs](../MainWindow.xaml.cs)

| Area | Key Methods | Purpose |
|---|---|---|
| Construction | `MainWindow()`, `EnforceMinimumWindowSize()` | Title bar setup, dispatcher capture, min size floor |
| Settings sync | `ApplySettingsToUi()`, `*_Changed` handlers | UI <-> `App.Settings` synchronization |
| Theme | `ApplyTitleBarTheme()`, `ThemeBtn_Click()` | Light/Dark handling and title bar styling |
| Pickers | `AddFiles_Click()`, `AddFolder_Click()`, `SelectDest_Click()` | File/folder destination selection via Win32 dialogs |
| Drag-drop | `DropArea_DragOver()`, `DropArea_Drop()` | Add dropped files/folders |
| Source list | `ClearSources_Click()`, `SourceItem` | `ObservableCollection` backing list view |
| Destination history | `RebuildDestHistoryItems()`, `AddToDestinationHistory()` | Editable combo source/history management |
| Copy orchestration | `StartBtn_Click()`, `PauseBtn_Click()`, `CancelBtn_Click()` | Starts and controls engine runs |
| Network monitor | `StartNetworkMonitor()`, `StopNetworkMonitor()` | Optional throughput display |
| UI state machine | `SetUiState(bool isOperating)` | Disable/enable controls while running |

### Why This File Is Large

The project intentionally avoided full MVVM refactoring so far. Logic is grouped by regions and responsibilities instead.

---

## 5. Copy Pipeline (Most Important Subsystem)

Implementation: [Services/CopyEngine.cs](../Services/CopyEngine.cs)

### Public Surface (Conceptual)

```csharp
public interface ICopyItem
{
    string Path { get; }
    void SetStatus(ItemStatus status, string? error = null);
}

public sealed class CopyOptions
{
    public required string Destination { get; init; }
    public bool DeleteSource { get; init; }
    public ConflictPolicy Conflict { get; init; }
    public long SmallFileThresholdBytes { get; init; }
    public int MaxParallelSmallFiles { get; init; }
}

public sealed record CopyProgress(...);

public sealed class CopyEngine
{
    public CopyEngine(PauseTokenSource pause);
    public event Action<CopyProgress>? Progress;
    public static long Preflight(IEnumerable<ICopyItem> items, CancellationToken token = default);
    public Task<CopyTotals> RunAsync(IList<ICopyItem> items, CopyOptions options, CancellationToken token);
}
```

### Pipeline Stages

1. Preflight totals bytes for overall progress.
2. Split into small top-level files vs sequential work.
3. Phase A: parallel copy for small files (`Parallel.ForEachAsync`).
4. Phase B: sequential traversal/copy for the rest (directories + large files).

### Copy Hot Loop (Key Perf Behavior)

- Async `FileStream`
- `SequentialScan`
- destination pre-allocation
- 1 MiB double-buffered pipelining
- periodic throttled progress reporting

### Conflict Policies

Defined in [Services/Enums.cs](../Services/Enums.cs):

- `Overwrite`
- `Skip`
- `SkipIfSame`
- `Rename`

### Pause/Resume

Implemented in [Services/PauseTokenSource.cs](../Services/PauseTokenSource.cs) using a `TaskCompletionSource<bool>` gate and cancellation-aware waits.

---

## 6. Threading & Concurrency Model

- UI triggers runs from the WinUI thread.
- Engine work runs on thread-pool workers.
- Engine never touches WinUI controls directly.
- UI updates are marshaled via `App.UiDispatcher.TryEnqueue(...)`.

Rules of thumb:

1. UI code can touch controls directly.
2. Engine/background code must enqueue to dispatcher for UI updates.
3. Shared counters use `Interlocked` and progress publication is serialized.

Common failure modes:

- Wrong-thread UI access exceptions
- Missing pause checks in new copy paths
- Missing cancellation checks in CPU loops

---

## 7. Settings, Logging, and Supporting Services

### Settings

- DTO: [Services/AppSettings.cs](../Services/AppSettings.cs)
- Persistence: [Services/SettingsService.cs](../Services/SettingsService.cs)
- Stored as JSON near executable (`AppContext.BaseDirectory/settings.json`)
- Uses source-generated `System.Text.Json` context

### Logging

- Wrapper: [Services/LoggingService.cs](../Services/LoggingService.cs)
- Serilog with rolling file sink under `logs/` and debug sink

### Notifications

- [Services/NotificationService.cs](../Services/NotificationService.cs)
- Completion toast via Windows App Notifications

### Network Monitor

- [Services/NetworkMonitor.cs](../Services/NetworkMonitor.cs)
- Polling-based throughput sampling for status display

### Power Management

- [Services/PowerService.cs](../Services/PowerService.cs)
- Uses `SetThreadExecutionState` while active

### Theme

- [Services/ThemeService.cs](../Services/ThemeService.cs)
- Applies root `RequestedTheme`; title bar color handled separately in main window code

### Dialogs

- [Services/Win32Dialogs.cs](../Services/Win32Dialogs.cs)
- Uses `IFileOpenDialog` with per-purpose `ClientGuid` to avoid WinRT picker UX/MRU issues

---

## 8. Bridge Type: SourceItem

Located in [MainWindow.xaml.cs](../MainWindow.xaml.cs).

`SourceItem` implements both:

- `INotifyPropertyChanged` for UI binding
- `ICopyItem` for engine integration

This is the key bridge between UI rows and copy engine status updates.

---

## 9. Build, Packaging, and Shipping

| Area | Unpackaged (default) | Packaged (MSIX) |
|---|---|---|
| `WindowsPackageType` | `None` | `Msix` |
| Output | Loose publish files | `.msix` package |
| Identity | No package identity | Has package identity |
| Install | Copy folder / run exe | App installer / Store |
| Current shipping model | Yes | No |

Project and pipeline files:

- [RoboCopyGUI.csproj](../RoboCopyGUI.csproj)
- [.github/workflows/ci.yml](../.github/workflows/ci.yml)

### CI Notes

1. `test` job on Ubuntu runs tests from [Tests/RoboCopyGUI.Tests.csproj](../Tests/RoboCopyGUI.Tests.csproj).
2. `build-and-publish` matrix on Windows publishes win-x64 and win-arm64 artifacts.

### Test Project Linking Pattern

The test project source-links selected service files instead of referencing the WinUI app project, enabling plain `net8.0` test runs.

---

## 10. Tests

Tests live in [Tests](../Tests):

- [Tests/CopyEngineTests.cs](../Tests/CopyEngineTests.cs)
- [Tests/PauseTokenSourceTests.cs](../Tests/PauseTokenSourceTests.cs)
- [Tests/TestHelpers.cs](../Tests/TestHelpers.cs)

Coverage includes:

- Single-file and recursive copy
- Move mode behavior
- Conflict policy behavior
- Partial failure resilience
- Cancellation/preflight
- Parallel small-file copy path
- Pause/resume behavior

Known gaps include UI-thread-specific behavior and some edge collision/guard scenarios.

---

## 11. Where to Make Common Changes

| Change You Want | Primary File(s) |
|---|---|
| Add UI control | [MainWindow.xaml](../MainWindow.xaml), [MainWindow.xaml.cs](../MainWindow.xaml.cs) |
| Add copy option | [Services/CopyEngine.cs](../Services/CopyEngine.cs), then wire from [MainWindow.xaml.cs](../MainWindow.xaml.cs) |
| Add persisted setting | [Services/AppSettings.cs](../Services/AppSettings.cs), [Services/SettingsService.cs](../Services/SettingsService.cs), [MainWindow.xaml.cs](../MainWindow.xaml.cs) |
| Tweak copy mechanics | [Services/CopyEngine.cs](../Services/CopyEngine.cs) |
| Change conflict behavior | [Services/Enums.cs](../Services/Enums.cs), [Services/CopyEngine.cs](../Services/CopyEngine.cs) |
| Add new status | [Services/Enums.cs](../Services/Enums.cs), [MainWindow.xaml.cs](../MainWindow.xaml.cs) |
| Add picker flow | [Services/Win32Dialogs.cs](../Services/Win32Dialogs.cs) |
| Customize title bar | [MainWindow.xaml](../MainWindow.xaml), [MainWindow.xaml.cs](../MainWindow.xaml.cs) |
| Change min window size | [MainWindow.xaml.cs](../MainWindow.xaml.cs) |
| Add CLI arg parsing | [App.xaml.cs](../App.xaml.cs) |

---

## 12. Conventions and Style Notes

- Nullable reference types enabled project-wide.
- Private fields use `_camelCase`.
- Prefer comments that explain why.
- No DI container currently; statics/globals are intentional for app size/scope.
- `async void` only for event handlers; elsewhere use `async Task`.
- Direct Serilog static usage is intentional.

---

## 13. Four Important "Magic" Bits

1. Custom title bar requires `ExtendsContentIntoTitleBar = true` plus `SetTitleBar(...)` before activation.
2. Dispatcher marshaling is mandatory for background-to-UI updates.
3. Reassigning editable `ComboBox.ItemsSource` clears text, so destination history is mutated in place.
4. `WindowsPackageType=None` controls the startup bootstrap path and prevents startup activation failures.

---

## 14. Suggested Reading Order

### Day 1: What It Does

1. [README.md](../README.md)
2. [App.xaml.cs](../App.xaml.cs)
3. [Services/Enums.cs](../Services/Enums.cs) and [Services/AppSettings.cs](../Services/AppSettings.cs)
4. [MainWindow.xaml](../MainWindow.xaml)
5. [Services/CopyEngine.cs](../Services/CopyEngine.cs)

### Day 2: UI Integration

1. [MainWindow.xaml.cs](../MainWindow.xaml.cs)
2. [Services/PauseTokenSource.cs](../Services/PauseTokenSource.cs)
3. [Services/Win32Dialogs.cs](../Services/Win32Dialogs.cs)

### Day 3: Shipping/Pipeline

1. [RoboCopyGUI.csproj](../RoboCopyGUI.csproj)
2. [.github/workflows/ci.yml](../.github/workflows/ci.yml)
3. [CHANGELOG.md](../CHANGELOG.md)

---

If you want, this guide can be split into smaller docs next:

- `docs/ARCHITECTURE_OVERVIEW.md`
- `docs/COPY_ENGINE.md`
- `docs/BUILD_AND_RELEASE.md`
- `docs/WINUI_NOTES.md`