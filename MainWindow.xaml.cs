using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RoboCopyGUI.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
using Windows.UI;

namespace RoboCopyGUI;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<SourceItem> _sourceItems = new();
    private readonly ObservableCollection<string> _destHistory = new();
    private readonly PauseTokenSource _pause = new();
    private readonly PowerService _power = new();
    private NetworkMonitor? _netMonitor;
    private CancellationTokenSource? _cts;
    private CopyEngine? _engine;
    private bool _isPaused;
    /// <summary>Re-entrancy guard — true while we're loading saved settings into the UI,
    /// so the change handlers don't write back partially-loaded state and corrupt the file.</summary>
    private bool _suppressSettingsSave;

    // Distinct ClientGuids per picker so Windows doesn't share an MRU between source and
    // destination dialogs (without these, picking a destination once would make the next
    // "Add Folder" open in the destination directory).
    private static readonly Guid PickerGuid_DestFolder    = new("3A3D3F6A-F1F4-4D6E-9E51-DEC4D2D58F17");
    private static readonly Guid PickerGuid_SourceFolder  = new("7B6D9A52-2E84-4E12-9F1F-1E3D2C7E9B3C");
    private static readonly Guid PickerGuid_SourceFiles   = new("5E20B8B4-8C7B-4D2F-9C3F-3F0DAA1B2233");

    public ObservableCollection<SourceItem> SourceItems => _sourceItems;

    private void UpdateSourceSummary()
    {
        int count = _sourceItems.Count;
        DispatcherQueue.TryEnqueue(() =>
        {
            // Hide (don't just dim) the watermark once the queue has items so the
            // listview gets the full drop-area surface — avoids overlap at low
            // window heights and keeps the layout from reserving icon-sized space.
            DropHintPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    public MainWindow()
    {
        this.InitializeComponent();
        // Capture the UI dispatcher for background services to marshal updates.
        App.UiDispatcher = DispatcherQueue;

        // Custom, themed title bar so it follows Light/Dark mode instead of the default
        // OS-painted strip. Must happen before Activate() to take effect.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppTitleText.Text = GetWindowTitle();

        // Bind the dest-history dropdown ONCE; afterwards we just mutate _destHistory in
        // place. Reassigning ItemsSource on an editable ComboBox clears the editable
        // text (which made the destination appear blank during a copy run).
        DestPathCombo.ItemsSource = _destHistory;

        UpdateSourceSummary();
        Title = GetWindowTitle();
        ApplySettingsToUi();
        ThemeService.Apply(this, App.Settings.Theme);
        UpdateThemeButtonLabel();
        ApplyTitleBarTheme();
        EnforceMinimumWindowSize();
        // Re-enforce the minimum on every user resize. The handler is idempotent
        // (only resizes back when smaller than the floor) so it can't loop.
        SizeChanged += (_, _) => EnforceMinimumWindowSize();
        Closed += (_, _) =>
        {
            _power.Release();
            StopNetworkMonitor();
        };

        // Fire the update check after the window is fully constructed so an InfoBar
        // can appear without racing with InitializeComponent / theme application.
        StartSelfUpdateCheck();
    }

    /// <summary>Logical-pixel floor below which the layout starts to break / clip.
    /// Picked empirically from the at-default-DPI screenshot.</summary>
    private const int MinWindowWidth  = 720;
    private const int MinWindowHeight = 560;

    /// <summary>Resize the AppWindow back up if it's currently below the minimum.
    /// WinUI 3 doesn't expose Window.MinWidth/Height, so we enforce on each
    /// SizeChanged event instead.</summary>
    private void EnforceMinimumWindowSize()
    {
        try
        {
            var aw = AppWindow;
            if (aw is null) return;
            var size = aw.Size;
            int w = Math.Max(size.Width,  MinWindowWidth);
            int h = Math.Max(size.Height, MinWindowHeight);
            if (w != size.Width || h != size.Height)
            {
                aw.Resize(new Windows.Graphics.SizeInt32(w, h));
            }
        }
        catch
        {
            // AppWindow APIs are best-effort here; a missing AppWindow shouldn't
            // crash the constructor or the resize loop.
        }
    }

    /// <summary>
    /// Push the current theme's foreground/background colors onto the AppWindow
    /// title-bar buttons so the caption bar looks right in Dark mode.
    /// </summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;

            var titleBar = AppWindow?.TitleBar;
            if (titleBar is null) return;

            // Determine the theme that's actually being rendered.
            ElementTheme actual = ElementTheme.Default;
            if (Content is FrameworkElement fe) actual = fe.ActualTheme;
            bool isDark = actual == ElementTheme.Dark
                || (actual == ElementTheme.Default &&
                    Application.Current?.RequestedTheme == ApplicationTheme.Dark);

            Color fg = isDark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x10, 0x10, 0x10);
            Color hoverBg = isDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00);
            Color pressedBg = isDark ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            Color inactiveFg = isDark ? Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xAA, 0x10, 0x10, 0x10);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonInactiveForegroundColor = inactiveFg;
            titleBar.ButtonHoverBackgroundColor = hoverBg;
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedBackgroundColor = pressedBg;
            titleBar.ButtonPressedForegroundColor = fg;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not apply title-bar theme.");
        }
    }

    private void ApplySettingsToUi()
    {
        var s = App.Settings;

        // Suppress save callbacks for the duration of this method — otherwise the first
        // checkbox we load fires its Checked handler, which reads the *other* checkboxes
        // (still at their XAML defaults) and writes those wrong values back to disk.
        _suppressSettingsSave = true;
        try
        {
            // Populate destination history dropdown (most-recent-first), making sure the
            // saved Destination is included even if it's not yet in history.
            RebuildDestHistoryItems();

            if (!string.IsNullOrWhiteSpace(s.Destination))
            {
                DestPathCombo.Text = s.Destination;
                if (Directory.Exists(s.Destination))
                {
                    Log.Debug("Restored destination from settings: {Dest}", s.Destination);
                }
                else
                {
                    Log.Information("Saved destination {Dest} no longer exists.", s.Destination);
                }
            }

            DeleteSourceCheck.IsChecked = s.DeleteSourceAfterCopy;
            NotifyOnDoneCheck.IsChecked = s.NotifyOnCompletion;
            PlaySoundCheck.IsChecked = s.PlaySoundOnCompletion;
            ShowNetCheck.IsChecked = s.ShowNetworkThroughput;
            CheckUpdatesCheck.IsChecked = s.CheckForUpdatesOnStartup;
            ParallelSmallBox.Value = Math.Clamp(s.MaxParallelSmallFiles, 1, 16);

            // Restore conflict policy selection (defaults to Overwrite if unknown).
            string tag = string.IsNullOrWhiteSpace(s.ConflictPolicy) ? "Overwrite" : s.ConflictPolicy;
            bool matched = false;
            foreach (var obj in ConflictPolicyCombo.Items)
            {
                if (obj is ComboBoxItem cbi &&
                    string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    ConflictPolicyCombo.SelectedItem = cbi;
                    matched = true;
                    break;
                }
            }
            if (!matched) ConflictPolicyCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private static void PersistSettingsFromUi(string destination, bool deleteSource)
    {
        App.Settings.Destination = destination ?? string.Empty;
        App.Settings.DeleteSourceAfterCopy = deleteSource;
        SettingsService.Save(App.Settings);
    }

    private static string GetWindowTitle()
    {
        const string baseTitle = "WinUI 3 File Copier";
        string? buildTime = GetBuildTimeStamp();
        return buildTime is null ? baseTitle : $"{baseTitle} - built {buildTime}";
    }

    private static string? GetBuildTimeStamp()
    {
        var assembly = Assembly.GetExecutingAssembly();
        if (string.IsNullOrEmpty(assembly.Location))
        {
            return null;
        }
        DateTime buildTime = File.GetLastWriteTime(assembly.Location);
        return buildTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // --- BUTTON HANDLERS ---

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceItems.Count == 0 || string.IsNullOrEmpty(DestPathCombo.Text)) return;

        ConflictPolicy conflict = ResolveConflictPolicy();
        bool deleteSource = DeleteSourceCheck.IsChecked == true;

        string destination = DestPathCombo.Text.Trim();
        PersistSettingsFromUi(destination, deleteSource);
        AddToDestinationHistory(destination);
        DestPathCombo.Text = destination; // history mutation can blank editable Text in WinUI ComboBox
        App.Settings.ConflictPolicy = conflict.ToString();
        SettingsService.Save(App.Settings);

        // Reset any non-Done items so a re-run of a partially-completed batch tries them again.
        foreach (var item in _sourceItems)
        {
            if (item.Status != ItemStatus.Done)
            {
                item.Reset();
            }
        }

        SetUiState(isOperating: true);
        _cts = new CancellationTokenSource();
        _pause.Resume();
        _isPaused = false;
        PauseBtn.Content = "Pause";

        if (App.Settings.KeepAwakeDuringCopy)
        {
            _power.KeepAwake();
        }

        StartNetworkMonitor();

        Log.Information("Copy started. Items={Count}, Destination={Dest}, Move={Move}, Conflict={Conflict}",
            _sourceItems.Count, destination, deleteSource, conflict);

        var engine = new CopyEngine(_pause);
        engine.Progress += OnEngineProgress;
        _engine = engine;

        bool failed = false;
        string finalStatus = "Ready";
        try
        {
            var pending = _sourceItems
                .Where(i => i.Status != ItemStatus.Done && i.Status != ItemStatus.Skipped)
                .Cast<ICopyItem>()
                .ToList();

            var totals = await engine.RunAsync(
                pending,
                new CopyOptions
                {
                    Destination = destination,
                    DeleteSource = deleteSource,
                    Conflict = conflict,
                    SmallFileThresholdBytes = App.Settings.SmallFileThresholdBytes,
                    MaxParallelSmallFiles = Math.Clamp(App.Settings.MaxParallelSmallFiles, 1, 16),
                },
                _cts.Token);

            string summary = FormatSummary(totals);
            StatusText.Text = summary;
            CopyProgressBar.Value = 100;
            failed = totals.Failed > 0;
            finalStatus = summary;
            Log.Information("Copy finished. {Summary}", summary);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled.";
            failed = true;
            finalStatus = "Cancelled";
            Log.Warning("Copy operation cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
            failed = true;
            finalStatus = $"Failed: {ex.Message}";
            Log.Error(ex, "Copy operation failed.");
        }
        finally
        {
            engine.Progress -= OnEngineProgress;
            _engine = null;
            _power.Release();
            StopNetworkMonitor();
            SetUiState(isOperating: false);
            NotificationService.NotifyCompletion(
                title: failed ? "RoboCopyGUI - finished with issues" : "RoboCopyGUI - copy finished",
                body: finalStatus,
                failed: failed);
        }
    }

    private void OnEngineProgress(CopyProgress p)
    {
        // Engine raises this from a worker thread; marshal to UI.
        DispatcherQueue.TryEnqueue(() =>
        {
            double percent = p.TotalBytesCurrentFile == 0
                ? 100
                : ((double)p.BytesCopiedCurrentFile / p.TotalBytesCurrentFile) * 100;
            CopyProgressBar.Value = percent;

            double overallPct = p.OverallBytesTotal == 0
                ? 0
                : ((double)p.OverallBytesCopied / p.OverallBytesTotal) * 100;
            OverallProgressBar.Value = overallPct;

            string etaText = p.Eta.TotalSeconds >= 1 ? $"  \u00B7  ETA {FormatTime(p.Eta)}" : string.Empty;
            StatusText.Text = $"Copying: {p.CurrentName}  \u2014  {p.InstantMBps:F1} MB/s{etaText}";
            OverallProgressText.Text = $"{FormatSize(p.OverallBytesCopied)} / {FormatSize(p.OverallBytesTotal)}  ({overallPct:F0}%)";
        });
    }

    private ConflictPolicy ResolveConflictPolicy()
    {
        string? tag = (ConflictPolicyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<ConflictPolicy>(tag, ignoreCase: true, out var policy)
            ? policy
            : ConflictPolicy.Overwrite;
    }

    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPaused)
        {
            _pause.Pause();
            PauseBtn.Content = "Resume";
            _isPaused = true;
        }
        else
        {
            _pause.Resume();
            PauseBtn.Content = "Pause";
            _isPaused = false;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // If we're paused when the user cancels, release the gate so the engine wakes up
        // to observe the cancellation token.
        _pause.Resume();
        _isPaused = false;
        _cts?.Cancel();
    }

    private void DeleteSourceCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        App.Settings.DeleteSourceAfterCopy = DeleteSourceCheck.IsChecked == true;
        SettingsService.Save(App.Settings);
    }

    private void NotificationToggles_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        App.Settings.NotifyOnCompletion = NotifyOnDoneCheck.IsChecked == true;
        App.Settings.PlaySoundOnCompletion = PlaySoundCheck.IsChecked == true;
        App.Settings.ShowNetworkThroughput = ShowNetCheck.IsChecked == true;
        SettingsService.Save(App.Settings);
        Log.Debug("Notification toggles saved: toast={Toast}, sound={Sound}, net={Net}",
            App.Settings.NotifyOnCompletion, App.Settings.PlaySoundOnCompletion, App.Settings.ShowNetworkThroughput);
        // If we're not currently copying, just hide/show NetText right away.
        if (_cts is null && NetText is not null)
        {
            NetText.Visibility = Visibility.Collapsed;
        }
    }

    private void CheckUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        App.Settings.CheckForUpdatesOnStartup = CheckUpdatesCheck.IsChecked == true;
        SettingsService.Save(App.Settings);
        Log.Debug("Check-for-updates set to {Enabled}", App.Settings.CheckForUpdatesOnStartup);
    }

    private async void UpdateOpenLink_Click(object sender, RoutedEventArgs e)
    {
        string url = (UpdateInfoBar.Tag as string) ?? SelfUpdateService.ReleasesPageUrl;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open update URL {Url}", url);
        }
    }

    /// <summary>
    /// Fire-and-forget self-update check. Runs on a background thread, never
    /// throws back to the UI, and only shows the InfoBar when a newer release
    /// actually exists. Caller controls opt-out via
    /// <see cref="AppSettings.CheckForUpdatesOnStartup"/>.
    /// </summary>
    private void StartSelfUpdateCheck()
    {
        if (!App.Settings.CheckForUpdatesOnStartup) return;

        _ = Task.Run(async () =>
        {
            string current = GetCurrentAssemblyVersion();
            var result = await SelfUpdateService.CheckAsync(current).ConfigureAwait(false);
            if (!result.HasUpdate || result.LatestVersion is null) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateInfoBar.Message = $"RoboCopyGUI {result.LatestVersion} is available (you have {current}).";
                UpdateInfoBar.Tag = result.ReleaseUrl ?? SelfUpdateService.ReleasesPageUrl;
                UpdateInfoBar.IsOpen = true;
                Log.Information("Update available: {Latest} (current {Current})", result.LatestVersion, current);
            });
        });
    }

    private static string GetCurrentAssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion)) return infoVersion;
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private void ParallelSmallBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSettingsSave) return;
        if (double.IsNaN(args.NewValue)) return;
        int v = Math.Clamp((int)args.NewValue, 1, 16);
        if (App.Settings.MaxParallelSmallFiles == v) return;
        App.Settings.MaxParallelSmallFiles = v;
        SettingsService.Save(App.Settings);
    }

    private void ConflictPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        if ((ConflictPolicyCombo.SelectedItem as ComboBoxItem)?.Tag is string tag)
        {
            App.Settings.ConflictPolicy = tag;
            SettingsService.Save(App.Settings);
            Log.Debug("Conflict policy set to {Policy}", tag);
        }
    }

    // --- Network throughput monitor ---

    private void StartNetworkMonitor()
    {
        if (!App.Settings.ShowNetworkThroughput) return;
        StopNetworkMonitor();
        _netMonitor = new NetworkMonitor();
        _netMonitor.Sampled += OnNetworkSampled;
        DispatcherQueue.TryEnqueue(() => NetText.Visibility = Visibility.Visible);
    }

    private void StopNetworkMonitor()
    {
        if (_netMonitor is null) return;
        _netMonitor.Sampled -= OnNetworkSampled;
        _netMonitor.Dispose();
        _netMonitor = null;
        DispatcherQueue.TryEnqueue(() => NetText.Visibility = Visibility.Collapsed);
    }

    private void OnNetworkSampled()
    {
        var mon = _netMonitor;
        if (mon is null) return;
        double upMb = mon.SendBytesPerSecond / (1024d * 1024d);
        double dnMb = mon.ReceiveBytesPerSecond / (1024d * 1024d);
        string adapter = string.IsNullOrEmpty(mon.AdapterName) ? "network" : mon.AdapterName;
        string text = $"Net ({adapter}):  \u2191 {upMb:F1} MB/s   \u2193 {dnMb:F1} MB/s";
        DispatcherQueue.TryEnqueue(() => NetText.Text = text);
    }

    // --- Destination history (editable ComboBox) ---

    /// <summary>
    /// Refreshes the dropdown items from <see cref="AppSettings.DestinationHistory"/>,
    /// preserving whatever the user has currently typed in the editable text portion.
    /// We mutate the bound ObservableCollection in-place; reassigning ItemsSource clears
    /// the editable Text on a ComboBox.
    /// </summary>
    private void RebuildDestHistoryItems()
    {
        var items = new List<string>(App.Settings.DestinationHistory);
        if (!string.IsNullOrWhiteSpace(App.Settings.Destination)
            && !items.Contains(App.Settings.Destination, StringComparer.OrdinalIgnoreCase))
        {
            items.Insert(0, App.Settings.Destination);
        }

        // Remove items that no longer belong; add new ones at their target positions.
        // Compare case-insensitively.
        for (int i = _destHistory.Count - 1; i >= 0; i--)
        {
            if (!items.Any(s => string.Equals(s, _destHistory[i], StringComparison.OrdinalIgnoreCase)))
            {
                _destHistory.RemoveAt(i);
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            string target = items[i];
            int existing = -1;
            for (int j = 0; j < _destHistory.Count; j++)
            {
                if (string.Equals(_destHistory[j], target, StringComparison.OrdinalIgnoreCase))
                {
                    existing = j;
                    break;
                }
            }
            if (existing == -1)
            {
                _destHistory.Insert(i, target);
            }
            else if (existing != i)
            {
                _destHistory.Move(existing, i);
            }
        }
    }

    /// <summary>Fired when the user picks an item from the dropdown.</summary>
    private void DestPathCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DestPathCombo.SelectedItem is string picked && !string.IsNullOrWhiteSpace(picked))
        {
            DestPathCombo.Text = picked; // keep editable text in sync; user can still edit afterwards
            App.Settings.Destination = picked;
            SettingsService.Save(App.Settings);
        }
    }

    /// <summary>Fired when the user presses Enter in the editable text area.</summary>
    private void DestPathCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        string typed = (args.Text ?? string.Empty).Trim();
        if (typed.Length == 0) return;
        // Don't auto-add to history just because the user typed a path; only add when they actually
        // start a copy. But persist as the current destination so it's restored next launch.
        App.Settings.Destination = typed;
        SettingsService.Save(App.Settings);
    }

    private void AddToDestinationHistory(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return;
        var hist = App.Settings.DestinationHistory ??= new List<string>();
        // Move-to-front, case-insensitive de-dup.
        hist.RemoveAll(p => string.Equals(p, destination, StringComparison.OrdinalIgnoreCase));
        hist.Insert(0, destination);
        if (hist.Count > AppSettings.MaxDestinationHistory)
        {
            hist.RemoveRange(AppSettings.MaxDestinationHistory, hist.Count - AppSettings.MaxDestinationHistory);
        }
        SettingsService.Save(App.Settings);
        RebuildDestHistoryItems();
    }

    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        string next = ThemeService.Cycle(App.Settings.Theme);
        App.Settings.Theme = next;
        SettingsService.Save(App.Settings);
        ThemeService.Apply(this, next);
        UpdateThemeButtonLabel();
        ApplyTitleBarTheme();
    }

    private void UpdateThemeButtonLabel()
    {
        // Segoe Fluent Icons glyphs:
        //   E706 = Brightness (sun)  -> Light
        //   E708 = QuietHours (moon) -> Dark
        //   EC46 = LightbulbCircle   -> System (auto)
        (string glyph, string tip) = App.Settings.Theme switch
        {
            ThemeService.Light => ("\uE706", "Theme: Light (click for Dark)"),
            ThemeService.Dark  => ("\uE708", "Theme: Dark (click for System)"),
            _                  => ("\uEC46", "Theme: System (click for Light)"),
        };
        ThemeIcon.Glyph = glyph;
        ToolTipService.SetToolTip(ThemeBtn, tip);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        IReadOnlyList<string> files = Win32Dialogs.PickFiles(hwnd, App.Settings.LastSourceFolder, PickerGuid_SourceFiles);
        if (files.Count == 0) return;

        bool added = false;
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (_sourceItems.Any(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new SourceItem(f);
            _sourceItems.Add(item);
            // If a copy is in flight, also enroll the item in the engine's live queue
            // so it gets picked up on the next outer-loop iteration.
            _engine?.AddItem(item);
            added = true;
        }
        if (added)
        {
            try
            {
                // Remember the folder the picked file lives in so the next Add Files
                // dialog opens there directly.
                string? dir = Path.GetDirectoryName(files[0]);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    App.Settings.LastSourceFolder = dir;
                    SettingsService.Save(App.Settings);
                    Log.Debug("LastSourceFolder set to {Dir}", dir);
                }
            }
            catch { /* best-effort */ }
            UpdateSourceSummary();
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        string? folder = Win32Dialogs.PickFolder(hwnd, App.Settings.LastSourceFolder, PickerGuid_SourceFolder, prefillLeafForReselect: false);
        if (string.IsNullOrWhiteSpace(folder)) return;

        if (_sourceItems.Any(i => string.Equals(i.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var item = new SourceItem(folder);
        _sourceItems.Add(item);
        _engine?.AddItem(item);
        try
        {
            // Remember the folder itself so the next Add Folder dialog lands inside it.
            App.Settings.LastSourceFolder = folder;
            SettingsService.Save(App.Settings);
            Log.Debug("LastSourceFolder set to {Dir}", folder);
        }
        catch { /* best-effort */ }
        UpdateSourceSummary();
    }

    private void DropArea_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Drop files or folders here";
        e.DragUIOverride.IsContentVisible = true;
        e.Handled = true;
    }

    private async void DropArea_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                bool added = false;
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Path))
                    {
                        continue;
                    }

                    bool alreadyAdded = _sourceItems.Any(existing =>
                        string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase));
                    if (alreadyAdded)
                    {
                        continue;
                    }

                    var src = new SourceItem(item.Path);
                    _sourceItems.Add(src);
                    _engine?.AddItem(src);
                    added = true;
                }
                if (added) UpdateSourceSummary();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SourceItem item) return;

        // If a copy is in flight, tell the engine first so it stops considering the
        // item. The engine's TryRemovePending only succeeds while the item is still
        // Queued — if the engine just claimed it, the request is a no-op and the
        // item finishes naturally. Either way, drop it from the UI list.
        _engine?.TryRemovePending(item);
        _sourceItems.Remove(item);
        UpdateSourceSummary();
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        _sourceItems.Clear();
        UpdateSourceSummary();
    }

    private void SelectDest_Click(object sender, RoutedEventArgs e)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        string? initial = string.IsNullOrWhiteSpace(DestPathCombo.Text)
            ? App.Settings.Destination
            : DestPathCombo.Text;

        string? folder = Win32Dialogs.PickFolder(hwnd, initial, PickerGuid_DestFolder, prefillLeafForReselect: true);
        if (string.IsNullOrWhiteSpace(folder)) return;

        AddToDestinationHistory(folder);
        DestPathCombo.Text = folder; // re-set after history mutation; bound collection edits can blank editable Text
        App.Settings.Destination = folder;
        SettingsService.Save(App.Settings);
        Log.Debug("Destination changed to {Dest}", folder);
        UpdateSourceSummary();
    }

    // --- COPY OPERATION (engine lives in Services/CopyEngine.cs) ---

    private static string FormatSummary(CopyTotals t)
    {
        double seconds = Math.Max(t.Elapsed.TotalSeconds, 0.001);
        double mbPerSec = t.TotalBytes / 1024.0 / 1024.0 / seconds;

        var parts = new System.Text.StringBuilder();
        parts.Append("Finished — ");
        parts.Append($"{FormatSize(t.TotalBytes)} in {FormatTime(t.Elapsed)}, avg {mbPerSec:F1} MB/s");
        parts.Append($"  ·  {t.Done} done");
        if (t.Skipped > 0) parts.Append($", {t.Skipped} skipped");
        if (t.Removed > 0) parts.Append($", {t.Removed} removed");
        if (t.Failed > 0)  parts.Append($", {t.Failed} failed");
        return parts.ToString();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:F2} {units[u]}";
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.TotalSeconds:F1}s";
    }

    private void SetUiState(bool isOperating)
    {
        StartBtn.IsEnabled = !isOperating;
        PauseBtn.IsEnabled = isOperating;
        CancelBtn.IsEnabled = isOperating;
        ClearListBtn.IsEnabled = !isOperating;
        BrowseDestBtn.IsEnabled = !isOperating;
        ConflictPolicyCombo.IsEnabled = !isOperating;
        // Dynamic queue: Add Files / Add Folder / drop stay live during a copy
        // so the user can append more work without waiting for the run to finish.
        AddFilesBtn.IsEnabled = true;
        AddFolderBtn.IsEnabled = true;
        DropArea.AllowDrop = true;
        CopyProgressBar.Visibility = isOperating ? Visibility.Visible : Visibility.Collapsed;
        OverallProgressBar.Visibility = isOperating ? Visibility.Visible : Visibility.Collapsed;
        OverallProgressText.Visibility = isOperating ? Visibility.Visible : Visibility.Collapsed;
        if (!isOperating)
        {
            CopyProgressBar.Value = 0;
            OverallProgressBar.Value = 0;
            OverallProgressText.Text = string.Empty;
        }
    }
}

public sealed class SourceItem : INotifyPropertyChanged, ICopyItem
{
    private ItemStatus _status = ItemStatus.Queued;
    private string? _error;

    public SourceItem(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public ItemStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(PathOpacity));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusToolTip));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(CompletionVisibility));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(RemoveVisibility));
        }
    }

    public string? ErrorMessage
    {
        get => _error;
        private set
        {
            if (_error == value) return;
            _error = value;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(StatusToolTip));
        }
    }

    public bool IsCompleted => _status == ItemStatus.Done;

    /// <summary>
    /// Path-row opacity. We use opacity (rather than custom brushes) so the foreground
    /// always comes from the active theme dictionary and re-themes correctly on toggle.
    /// </summary>
    public double PathOpacity => _status switch
    {
        ItemStatus.Done     => 0.55,
        ItemStatus.Skipped  => 0.55,
        ItemStatus.Removed  => 0.55,
        _                   => 1.0,
    };

    /// <summary>Segoe Fluent Icons glyph that represents the current status.</summary>
    public string StatusGlyph => _status switch
    {
        ItemStatus.Queued     => "\uE823", // Clock
        ItemStatus.InProgress => "\uE895", // Sync
        ItemStatus.Done       => "\uE73E", // CheckMark
        ItemStatus.Failed     => "\uEB90", // ErrorBadge
        ItemStatus.Skipped    => "\uE7E8", // SkipForward / Forward
        ItemStatus.Removed    => "\uE894", // Cancel
        _ => "\uE823",
    };

    public Brush StatusBrush => _status switch
    {
        ItemStatus.Done    => new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x7C, 0x10)),
        ItemStatus.Failed  => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
        ItemStatus.Skipped => new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80)),
        ItemStatus.Removed => new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80)),
        _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x60, 0x60)),
    };

    public string StatusToolTip => _status switch
    {
        ItemStatus.Failed when !string.IsNullOrEmpty(_error) => $"Failed: {_error}",
        _ => _status.ToString(),
    };

    /// <summary>
    /// True only while the item is still waiting in the queue — that's the only
    /// time the per-row Remove button is meaningful. Once the engine has claimed
    /// the item (InProgress) or it has terminated, removal is no longer offered.
    /// </summary>
    public bool CanRemove => _status == ItemStatus.Queued;

    /// <summary>Bound directly by the per-row Remove button's <c>Visibility</c>.</summary>
    public Visibility RemoveVisibility =>
        _status == ItemStatus.Queued ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Kept for back-compat with any older XAML bindings; not used after Phase 2.</summary>
    public Visibility CompletionVisibility => IsCompleted ? Visibility.Visible : Visibility.Collapsed;

    public void SetStatus(ItemStatus status, string? error = null)
    {
        // Engine calls this from a worker thread. Marshal to the UI dispatcher captured
        // by App at startup so {x:Bind} property-change notifications fire on the right thread.
        var dq = App.UiDispatcher;
        if (dq is null || dq.HasThreadAccess)
        {
            ApplyStatus(status, error);
        }
        else
        {
            dq.TryEnqueue(() => ApplyStatus(status, error));
        }
    }

    private void ApplyStatus(ItemStatus status, string? error)
    {
        ErrorMessage = error;
        Status = status;
    }

    public void Reset()
    {
        ApplyStatus(ItemStatus.Queued, null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}