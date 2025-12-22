using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
    private ObservableCollection<SourceItem> _sourceItems = new();
    private CancellationTokenSource? _cts;
    private ManualResetEventSlim _pauseEvent = new(true); // True = Not Paused
    private bool _isPaused = false;

    public ObservableCollection<SourceItem> SourceItems => _sourceItems;

    private void UpdateSourceSummary()
    {
        int count = _sourceItems.Count;
        string text = $"{count} item{(count == 1 ? string.Empty : "s")} selected";
        DispatcherQueue.TryEnqueue(() => FileCountText.Text = text);
    }

    public MainWindow()
    {
        this.InitializeComponent();
        UpdateSourceSummary();
        Title = GetWindowTitle();
    }

    private static string GetWindowTitle()
    {
        const string baseTitle = "WinUI 3 File Copier";
        string buildTime = GetBuildTimeStamp();
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
        if (_sourceItems.Count == 0 || string.IsNullOrEmpty(DestPathText.Text)) return;

        SetUiState(isOperating: true);
        _cts = new CancellationTokenSource();
        _pauseEvent.Set();

        try
        {
            await RunCopyOperation(_cts.Token);
            StatusText.Text = "Finished successfully!";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled.";
        }
        finally
        {
            SetUiState(isOperating: false);
        }
    }

    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPaused)
        {
            _pauseEvent.Reset(); // Logic will block at Wait()
            PauseBtn.Content = "Resume";
            _isPaused = true;
        }
        else
        {
            _pauseEvent.Set(); // Logic continues
            PauseBtn.Content = "Pause";
            _isPaused = false;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

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

                    _sourceItems.Add(new SourceItem(item.Path));
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

    private void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        _sourceItems.Clear();
        UpdateSourceSummary();
    }

    private async void SelectDest_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            DestPathText.Text = folder.Path;
        }
        UpdateSourceSummary();
    }

    // --- CORE COPY LOGIC ---

    private async Task RunCopyOperation(CancellationToken token)
    {
        string destination = DestPathText.Text;
        bool deleteSource = DeleteSourceCheck.IsChecked ?? false;

        var itemsToCopy = _sourceItems
            .Where(item => !item.IsCompleted)
            .ToList();

        if (itemsToCopy.Count == 0)
        {
            return;
        }

        void UpdateCurrentFileProgress(long copied, long total)
        {
            double progress = total == 0 ? 100 : ((double)copied / total) * 100;
            DispatcherQueue.TryEnqueue(() => CopyProgressBar.Value = progress);
        }

        for (int i = 0; i < itemsToCopy.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Run(() => _pauseEvent.Wait(token));

            SourceItem item = itemsToCopy[i];
            string fileName = Path.GetFileName(item.Path);
            string destFile = Path.Combine(destination, fileName);

            DispatcherQueue.TryEnqueue(() =>
            {
                StatusText.Text = $"Copying: {fileName}";
                CopyProgressBar.Value = 0;
            });

            await Task.Run(() =>
            {
                if (Directory.Exists(item.Path))
                {
                    CopyDirectoryWithProgress(item.Path, destFile, deleteSource, token, UpdateCurrentFileProgress);
                }
                else
                {
                    CopyFileWithProgress(item.Path, destFile, deleteSource, token, UpdateCurrentFileProgress);
                }
            }, token);

            DispatcherQueue.TryEnqueue(() =>
            {
                item.MarkCompleted();
            });
        }
    }

    private static void CopyDirectoryWithProgress(string sourceDir, string destDir, bool deleteAfter, CancellationToken token, Action<long, long> reportProgress)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            token.ThrowIfCancellationRequested();
            string dest = Path.Combine(destDir, Path.GetFileName(file));
            CopyFileWithProgress(file, dest, deleteAfter, token, reportProgress);
        }
        foreach (string folder in Directory.GetDirectories(sourceDir))
        {
            token.ThrowIfCancellationRequested();
            CopyDirectoryWithProgress(folder, Path.Combine(destDir, Path.GetFileName(folder)), deleteAfter, token, reportProgress);
        }
        if (deleteAfter)
        {
            Directory.Delete(sourceDir, true);
        }
    }

    private static void CopyFileWithProgress(string sourceFile, string destFile, bool deleteAfter, CancellationToken token, Action<long, long> reportProgress)
    {
        long totalBytes = new FileInfo(sourceFile).Length;
        byte[] buffer = new byte[81920];
        int bytesRead;
        long totalRead = 0;
        using (FileStream sourceStream = File.Open(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream destStream = File.Open(destFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                destStream.Write(buffer, 0, bytesRead);
                totalRead += bytesRead;
                reportProgress(totalRead, totalBytes);
            }
            reportProgress(totalBytes, totalBytes);
        }

        if (deleteAfter)
        {
            File.Delete(sourceFile);
        }
    }

    private void SetUiState(bool isOperating)
    {
        StartBtn.IsEnabled = !isOperating;
        PauseBtn.IsEnabled = isOperating;
        CancelBtn.IsEnabled = isOperating;
        ClearListBtn.IsEnabled = !isOperating;
        BrowseDestBtn.IsEnabled = !isOperating;
        DropArea.AllowDrop = !isOperating;
        CopyProgressBar.Visibility = isOperating ? Visibility.Visible : Visibility.Collapsed;
        if (!isOperating) CopyProgressBar.Value = 0;
    }
}

public sealed class SourceItem : INotifyPropertyChanged
{
    private bool _isCompleted;

    public SourceItem(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public bool IsCompleted
    {
        get => _isCompleted;
        private set
        {
            if (_isCompleted == value)
            {
                return;
            }

            _isCompleted = value;
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(TextBrush));
            OnPropertyChanged(nameof(CompletionVisibility));
        }
    }

    public Brush TextBrush
    {
        get
        {
            if (IsCompleted)
            {
                return new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A));
            }

            if (Application.Current is not null &&
                Application.Current.Resources.TryGetValue("SystemControlForegroundBaseHighBrush", out var brush) &&
                brush is Brush themeBrush)
            {
                return themeBrush;
            }

            return new SolidColorBrush(Color.FromArgb(0xFF, 0x21, 0x21, 0x21));
        }
    }

    public Visibility CompletionVisibility => IsCompleted ? Visibility.Visible : Visibility.Collapsed;

    public void MarkCompleted() => IsCompleted = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}