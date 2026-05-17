using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>
/// A queued item the engine should copy. Implemented by the UI's <c>SourceItem</c>
/// so we never depend on WinUI types here, keeping the engine unit-testable.
/// </summary>
public interface ICopyItem
{
    string Path { get; }

    /// <summary>Notify the UI of a status change. Optional error message for <c>Failed</c>.</summary>
    void SetStatus(ItemStatus status, string? error = null);
}

/// <summary>Knobs passed to a single <see cref="CopyEngine.RunAsync"/> invocation.</summary>
public sealed class CopyOptions
{
    public required string Destination { get; init; }
    public bool DeleteSource { get; init; }
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Overwrite;

    /// <summary>Files at or below this size (bytes) are eligible for parallel copy.</summary>
    public long SmallFileThresholdBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>Max concurrent small-file copies. 1 disables parallelism.</summary>
    public int MaxParallelSmallFiles { get; init; } = 1;
}

/// <summary>Aggregate result of a copy run.</summary>
public sealed class CopyTotals
{
    public long TotalBytes { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }

    /// <summary>
    /// Items the user removed from the queue (via <see cref="CopyEngine.TryRemovePending"/>)
    /// before the engine started copying them. Counted separately from Skipped so the
    /// summary line can distinguish "I asked the engine to skip these" from "the user
    /// changed their mind".
    /// </summary>
    public int Removed { get; set; }
}

/// <summary>Live progress payload pushed by the engine.</summary>
public sealed record CopyProgress(
    string CurrentName,
    long BytesCopiedCurrentFile,
    long TotalBytesCurrentFile,
    double InstantMBps,
    long OverallBytesCopied,
    long OverallBytesTotal,
    TimeSpan Eta);

/// <summary>
/// UI-independent file-copy engine. Pipelines async I/O with 1 MiB buffers, supports
/// per-file resilience, conflict resolution, pause/resume and cancellation.
/// </summary>
/// <remarks>
/// All file-system access is routed through an injected <see cref="IFileSystem"/>
/// (defaults to <see cref="RealFileSystem.Instance"/>), so the engine is unit-testable
/// against an in-memory fake without touching real disk.
/// </remarks>
public sealed class CopyEngine
{
    public const int CopyBufferSize = 1 * 1024 * 1024;
    private const int ProgressUpdateIntervalMs = 150;

    private readonly PauseTokenSource _pause;
    private readonly IFileSystem _fs;

    // ---- live queue state -------------------------------------------------
    // All access to _queue / _state / _itemSizes / _completedBytes is serialized
    // through _lock. The lock is held only briefly (queue mutation + status
    // transitions); long work (Preflight measurement, actual I/O) runs outside it.
    // Locks are NEVER held while invoking Progress, item.SetStatus, or other
    // callbacks — that prevents deadlocks and re-entrancy.
    private readonly object _lock = new();
    private readonly List<ICopyItem> _queue = new();
    private readonly Dictionary<ICopyItem, ItemStatus> _state =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ICopyItem, long> _itemSizes =
        new(ReferenceEqualityComparer.Instance);
    private long _completedBytes;
    private bool _hasRun;

    public CopyEngine(PauseTokenSource pause, IFileSystem? fs = null)
    {
        _pause = pause;
        _fs = fs ?? RealFileSystem.Instance;
    }

    /// <summary>Fired (throttled) while a file is being copied.</summary>
    public event Action<CopyProgress>? Progress;

    /// <summary>
    /// Walk the queued items and total their sizes against the real file system.
    /// Convenience overload preserved for backward compatibility with callers
    /// that don't supply an <see cref="IFileSystem"/>.
    /// </summary>
    public static long Preflight(IEnumerable<ICopyItem> items, CancellationToken token = default) =>
        Preflight(items, RealFileSystem.Instance, token);

    /// <summary>
    /// Walk the queued items and total their sizes against the supplied
    /// <paramref name="fs"/>. Cheap for files; for directories it recursively sums
    /// file lengths. Safe to call before <see cref="RunAsync"/>. Errors enumerating
    /// a particular item are swallowed (counted as 0) so the preflight never blocks
    /// a copy from starting.
    /// </summary>
    public static long Preflight(IEnumerable<ICopyItem> items, IFileSystem fs, CancellationToken token = default)
    {
        long total = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            total += MeasureItemSize(item, fs, token);
        }
        return total;
    }

    private static long MeasureItemSize(ICopyItem item, IFileSystem fs, CancellationToken token)
    {
        try
        {
            if (fs.DirectoryExists(item.Path))
            {
                long sum = 0;
                foreach (var f in fs.EnumerateFiles(item.Path, recurse: true))
                {
                    token.ThrowIfCancellationRequested();
                    try { sum += fs.GetFileSize(f); }
                    catch { /* unreadable file — skip */ }
                }
                return sum;
            }
            if (fs.FileExists(item.Path))
            {
                return fs.GetFileSize(item.Path);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Preflight could not size {Path}", item.Path);
        }
        return 0;
    }

    // ---- dynamic queue API ------------------------------------------------

    /// <summary>
    /// Add an item to the live queue. Safe to call at any time — including
    /// while <see cref="RunAsync"/> is executing. The engine picks the new
    /// item up on its next outer-loop iteration. Idempotent for the same
    /// item reference. Notifies the item with <see cref="ItemStatus.Queued"/>.
    /// </summary>
    public void AddItem(ICopyItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        bool added;
        lock (_lock)
        {
            if (_state.ContainsKey(item)) { added = false; }
            else
            {
                _queue.Add(item);
                _state[item] = ItemStatus.Queued;
                added = true;
            }
        }
        if (added) item.SetStatus(ItemStatus.Queued);
    }

    /// <summary>
    /// Atomically mark a still-pending item as <see cref="ItemStatus.Removed"/>
    /// so the engine skips it. Returns <c>true</c> if the item was found in
    /// <see cref="ItemStatus.Queued"/> state and successfully removed; <c>false</c>
    /// if the item is unknown OR has already advanced past Queued (already
    /// InProgress / Done / Failed / Skipped / Removed). The latter is the
    /// expected "user clicked Remove just as the file started copying" race.
    /// </summary>
    public bool TryRemovePending(ICopyItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        bool removed;
        lock (_lock)
        {
            removed = _state.TryGetValue(item, out var s) && s == ItemStatus.Queued;
            if (removed) _state[item] = ItemStatus.Removed;
        }
        if (removed) item.SetStatus(ItemStatus.Removed);
        return removed;
    }

    /// <summary>
    /// Snapshot of items currently in the engine's queue, in insertion order.
    /// Each item's <see cref="GetStatus"/> reflects the engine's view at call time.
    /// </summary>
    public IReadOnlyList<ICopyItem> Snapshot()
    {
        lock (_lock) return _queue.ToArray();
    }

    /// <summary>Engine's authoritative status for <paramref name="item"/>, or null if unknown.</summary>
    public ItemStatus? GetStatus(ICopyItem item)
    {
        lock (_lock) return _state.TryGetValue(item, out var s) ? s : null;
    }

    // Atomic Queued -> InProgress transition. Returns false if item was Removed
    // (or otherwise no longer Queued) between batch snapshot and worker start.
    private bool TryClaimItem(ICopyItem item)
    {
        lock (_lock)
        {
            if (_state.TryGetValue(item, out var s) && s == ItemStatus.Queued)
            {
                _state[item] = ItemStatus.InProgress;
                return true;
            }
            return false;
        }
    }

    // Roll back InProgress -> Queued on cancellation so a future Resume picks it up.
    private void RewindToQueued(ICopyItem item)
    {
        lock (_lock)
        {
            if (_state.TryGetValue(item, out var s) && s == ItemStatus.InProgress)
                _state[item] = ItemStatus.Queued;
        }
    }

    private void SetItemStatus(ICopyItem item, ItemStatus s, string? error = null)
    {
        lock (_lock) _state[item] = s;
        item.SetStatus(s, error);
    }

    // Sum of bytes the engine still expects to transfer: completed + everything
    // still Queued or InProgress. Removed/Failed/Skipped items drop out naturally,
    // so the live denominator shrinks when the user removes a pending item.
    // Caller must hold _lock.
    private long ComputeOverallTotalLocked()
    {
        long pending = 0;
        foreach (var item in _queue)
        {
            if (!_state.TryGetValue(item, out var s)) continue;
            if (s == ItemStatus.Queued || s == ItemStatus.InProgress)
            {
                if (_itemSizes.TryGetValue(item, out var b)) pending += b;
            }
        }
        return Interlocked.Read(ref _completedBytes) + pending;
    }

    public async Task<CopyTotals> RunAsync(
        IEnumerable<ICopyItem> initialItems,
        CopyOptions options,
        CancellationToken token)
    {
        if (initialItems is null) throw new ArgumentNullException(nameof(initialItems));
        lock (_lock)
        {
            if (_hasRun) throw new InvalidOperationException(
                "CopyEngine has already been used; create a new instance per copy run.");
            _hasRun = true;
        }

        foreach (var item in initialItems) AddItem(item);

        var totals = new CopyTotals();
        var overall = Stopwatch.StartNew();

        // Throttled, rolling speed counter. The current-file's partial bytes are
        // captured in `copied`; the cross-run completed count lives in _completedBytes
        // so AddItem / TryRemovePending can compute a live denominator from another thread.
        var lastTick = Stopwatch.StartNew();
        long bytesAtLastTick = 0;
        string currentName = string.Empty;
        double smoothedMBps = 0;
        var reportLock = new object();

        void Report(long copied, long total, bool force)
        {
            CopyProgress? payload = null;
            lock (reportLock)
            {
                if (!force && lastTick.ElapsedMilliseconds < ProgressUpdateIntervalMs) return;

                long ms = Math.Max(1, lastTick.ElapsedMilliseconds);
                long completed = Interlocked.Read(ref _completedBytes);
                long copiedOverall = completed + copied;
                long totalQueueBytes;
                lock (_lock) totalQueueBytes = ComputeOverallTotalLocked();
                long delta = copiedOverall - bytesAtLastTick;
                double mbps = delta / 1024.0 / 1024.0 * 1000.0 / ms;
                bytesAtLastTick = copiedOverall;
                lastTick.Restart();

                // Exponential moving average for stable ETA computation.
                smoothedMBps = smoothedMBps == 0 ? mbps : (smoothedMBps * 0.7) + (mbps * 0.3);

                long remaining = Math.Max(0, totalQueueBytes - copiedOverall);
                TimeSpan eta = TimeSpan.Zero;
                if (smoothedMBps > 0.1 && remaining > 0)
                {
                    double seconds = remaining / 1024.0 / 1024.0 / smoothedMBps;
                    eta = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1));
                }

                payload = new CopyProgress(
                    currentName,
                    copied, total,
                    mbps,
                    copiedOverall, totalQueueBytes,
                    eta);
            }
            // Invoke Progress OUTSIDE both locks so a re-entrant handler (or one
            // that takes a UI lock) can't deadlock with the engine.
            if (payload is not null) Progress?.Invoke(payload);
        }

        try
        {
            // Outer loop: each iteration drains the current set of Queued items.
            // New items added (via AddItem) between iterations are picked up
            // automatically by the next snapshot. Items removed (via
            // TryRemovePending) drop out before we get to them.
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await _pause.WaitWhilePausedAsync(token).ConfigureAwait(false);

                // Step 1: pick up any new Queued items we haven't measured yet.
                ICopyItem[] toMeasure;
                lock (_lock)
                {
                    toMeasure = _queue
                        .Where(i => _state[i] == ItemStatus.Queued && !_itemSizes.ContainsKey(i))
                        .ToArray();
                }

                // Step 2: measure them OUTSIDE the lock — a freshly-dropped 50k-file
                // directory would otherwise block AddItem / TryRemovePending.
                var measured = new Dictionary<ICopyItem, long>(ReferenceEqualityComparer.Instance);
                foreach (var item in toMeasure)
                {
                    token.ThrowIfCancellationRequested();
                    measured[item] = MeasureItemSize(item, _fs, token);
                }

                // Step 3: re-enter the lock, cache sizes, snapshot the batch.
                ICopyItem[] batch;
                lock (_lock)
                {
                    foreach (var kv in measured)
                        if (!_itemSizes.ContainsKey(kv.Key))
                            _itemSizes[kv.Key] = kv.Value;
                    batch = _queue.Where(i => _state[i] == ItemStatus.Queued).ToArray();
                }

                if (batch.Length == 0) break;

                // Step 4: classify the batch into small (parallel-eligible) vs
                // sequential (large files + directories). Uses the cached size
                // so we don't hit the FS twice.
                bool parallelEnabled = options.MaxParallelSmallFiles > 1;
                var smallTopLevel = new List<ICopyItem>();
                var sequentialTopLevel = new List<ICopyItem>();
                if (parallelEnabled)
                {
                    foreach (var item in batch)
                    {
                        long len;
                        lock (_lock)
                            len = _itemSizes.TryGetValue(item, out var b) ? b : long.MaxValue;
                        if (_fs.FileExists(item.Path) && len <= options.SmallFileThresholdBytes)
                            smallTopLevel.Add(item);
                        else
                            sequentialTopLevel.Add(item);
                    }
                }
                else
                {
                    sequentialTopLevel.AddRange(batch);
                }

                // Phase A: parallel pass over small top-level files.
                if (smallTopLevel.Count > 0)
                {
                    await Parallel.ForEachAsync(
                        smallTopLevel,
                        new ParallelOptions
                        {
                            CancellationToken = token,
                            MaxDegreeOfParallelism = Math.Min(options.MaxParallelSmallFiles, smallTopLevel.Count),
                        },
                        async (item, ct) =>
                        {
                            await _pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                            // Atomic Queued -> InProgress (user may have Removed
                            // since the batch was snapshotted; in that case skip).
                            if (!TryClaimItem(item)) return;
                            item.SetStatus(ItemStatus.InProgress);
                            string name = Path.GetFileName(item.Path);
                            try
                            {
                                string destFile = Path.Combine(options.Destination, name);
                                var r = await CopyOneFileAsync(item.Path, destFile, options, ct,
                                    // No per-buffer reporting in parallel: we'd interleave names. Just
                                    // refresh the displayed name so the user sees what's flowing.
                                    (_, _, _) => { lock (reportLock) currentName = name; }
                                ).ConfigureAwait(false);
                                Interlocked.Add(ref _completedBytes, r.Bytes);
                                lock (reportLock) { totals.TotalBytes += r.Bytes; if (r.Skipped) totals.Skipped++; else totals.Done++; }
                                SetItemStatus(item, r.Skipped ? ItemStatus.Skipped : ItemStatus.Done);
                                Report(0, 0, force: true);
                            }
                            catch (OperationCanceledException)
                            {
                                RewindToQueued(item);
                                item.SetStatus(ItemStatus.Queued);
                                throw;
                            }
                            catch (Exception ex)
                            {
                                lock (reportLock) totals.Failed++;
                                SetItemStatus(item, ItemStatus.Failed, ex.Message);
                                Log.Error(ex, "Failed to copy {Path}", item.Path);
                            }
                        }).ConfigureAwait(false);
                }

                // Phase B: sequential pass over directories + large files. This is the
                // original pipelined path; inside a directory we may still parallelize
                // *small* files belonging to that tree (CopyDirectoryAsync handles that).
                foreach (var item in sequentialTopLevel)
                {
                    token.ThrowIfCancellationRequested();
                    await _pause.WaitWhilePausedAsync(token).ConfigureAwait(false);

                    if (!TryClaimItem(item)) continue;
                    item.SetStatus(ItemStatus.InProgress);
                    lock (reportLock) currentName = Path.GetFileName(item.Path);

                    try
                    {
                        long bytes;
                        bool skippedTopLevel = false;
                        bool isDirItem = _fs.DirectoryExists(item.Path);

                        if (isDirItem)
                        {
                            string destDir = Path.Combine(options.Destination, Path.GetFileName(item.Path));
                            bytes = await CopyDirectoryAsync(item.Path, destDir, options, token,
                                setName: n => { lock (reportLock) currentName = n; },
                                report: Report,
                                addCompleted: b => Interlocked.Add(ref _completedBytes, b)).ConfigureAwait(false);
                            // CopyDirectoryAsync already incremented _completedBytes per file via
                            // addCompleted, so we must NOT add `bytes` again below.

                            if (options.DeleteSource)
                            {
                                try { _fs.DeleteDirectory(item.Path, recursive: true); }
                                catch (Exception ex) { Log.Warning(ex, "Could not delete source dir {Path}", item.Path); }
                            }
                        }
                        else if (_fs.FileExists(item.Path))
                        {
                            string destFile = Path.Combine(options.Destination, Path.GetFileName(item.Path));
                            var fileResult = await CopyOneFileAsync(item.Path, destFile, options, token, Report).ConfigureAwait(false);
                            bytes = fileResult.Bytes;
                            skippedTopLevel = fileResult.Skipped;
                            if (!skippedTopLevel) Interlocked.Add(ref _completedBytes, bytes);
                        }
                        else
                        {
                            throw new FileNotFoundException("Source no longer exists", item.Path);
                        }

                        totals.TotalBytes += bytes;

                        if (skippedTopLevel)
                        {
                            totals.Skipped++;
                            SetItemStatus(item, ItemStatus.Skipped);
                            Log.Information("Skipped (existing): {Path}", item.Path);
                        }
                        else
                        {
                            totals.Done++;
                            SetItemStatus(item, ItemStatus.Done);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        RewindToQueued(item);
                        item.SetStatus(ItemStatus.Queued);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        totals.Failed++;
                        SetItemStatus(item, ItemStatus.Failed, ex.Message);
                        Log.Error(ex, "Failed to copy {Path}", item.Path);
                        // Keep going to next item — per-file resilience.
                    }
                }
            }
        }
        finally
        {
            // Tally Removed at the end. Done/Failed/Skipped were counted at the
            // point of transition; Removed transitions happen externally
            // (TryRemovePending) and could fire any time, so we count once here.
            lock (_lock)
            {
                int removedCount = 0;
                foreach (var s in _state.Values)
                    if (s == ItemStatus.Removed) removedCount++;
                totals.Removed = removedCount;
            }
            overall.Stop();
            totals.Elapsed = overall.Elapsed;
        }
        return totals;
    }

    // -----------------------------------------------------------------------------------

    private async Task<long> CopyDirectoryAsync(
        string sourceDir,
        string destDir,
        CopyOptions options,
        CancellationToken token,
        Action<string> setName,
        Action<long, long, bool> report,
        Action<long> addCompleted)
    {
        _fs.CreateDirectory(destDir);
        long bytes = 0;

        // Split this folder's files: small ones go through the parallel path,
        // big ones stream sequentially with full per-buffer progress reporting.
        bool parallelEnabled = options.MaxParallelSmallFiles > 1;
        var smallFiles = new List<string>();
        var largeFiles = new List<string>();
        foreach (string file in _fs.EnumerateFiles(sourceDir, recurse: false))
        {
            token.ThrowIfCancellationRequested();
            long len;
            try { len = _fs.GetFileSize(file); }
            catch { len = long.MaxValue; }
            if (parallelEnabled && len <= options.SmallFileThresholdBytes)
                smallFiles.Add(file);
            else
                largeFiles.Add(file);
        }

        if (smallFiles.Count > 0)
        {
            await Parallel.ForEachAsync(
                smallFiles,
                new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = Math.Min(options.MaxParallelSmallFiles, smallFiles.Count),
                },
                async (file, ct) =>
                {
                    await _pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                    string dest = Path.Combine(destDir, Path.GetFileName(file));
                    setName(Path.GetFileName(file));
                    try
                    {
                        var r = await CopyOneFileAsync(file, dest, options, ct,
                            // No per-buffer progress here either (would interleave names);
                            // we account for the bytes at the end via addCompleted.
                            (_, _, _) => { }).ConfigureAwait(false);
                        Interlocked.Add(ref bytes, r.Bytes);
                        addCompleted(r.Bytes);
                        report(0, 0, true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Skipping file inside tree: {File}", file);
                    }
                }).ConfigureAwait(false);
        }

        foreach (string file in largeFiles)
        {
            token.ThrowIfCancellationRequested();
            await _pause.WaitWhilePausedAsync(token).ConfigureAwait(false);

            string dest = Path.Combine(destDir, Path.GetFileName(file));
            setName(Path.GetFileName(file));

            try
            {
                var result = await CopyOneFileAsync(file, dest, options, token, report).ConfigureAwait(false);
                Interlocked.Add(ref bytes, result.Bytes);
                addCompleted(result.Bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Per-file error inside a directory tree: log and continue with siblings.
                Log.Warning(ex, "Skipping file inside tree: {File}", file);
            }
        }

        foreach (string folder in _fs.EnumerateDirectories(sourceDir))
        {
            token.ThrowIfCancellationRequested();
            long sub = await CopyDirectoryAsync(
                folder,
                Path.Combine(destDir, Path.GetFileName(folder)),
                options, token, setName, report, addCompleted).ConfigureAwait(false);
            Interlocked.Add(ref bytes, sub);
        }

        return bytes;
    }

    private readonly record struct OneFileResult(long Bytes, bool Skipped);

    private async Task<OneFileResult> CopyOneFileAsync(
        string sourceFile,
        string destFile,
        CopyOptions options,
        CancellationToken token,
        Action<long, long, bool> report)
    {
        // Conflict resolution — may mutate destFile or short-circuit.
        if (_fs.FileExists(destFile))
        {
            switch (options.Conflict)
            {
                case ConflictPolicy.Skip:
                    return new OneFileResult(0, Skipped: true);

                case ConflictPolicy.SkipIfSame:
                    if (FilesLookEquivalent(_fs, sourceFile, destFile))
                        return new OneFileResult(0, Skipped: true);
                    break;

                case ConflictPolicy.Rename:
                    destFile = FindNonClashingName(_fs, destFile);
                    break;

                case ConflictPolicy.Overwrite:
                default:
                    break; // OpenWrite below will create/truncate.
            }
        }

        long totalBytes = _fs.GetFileSize(sourceFile);

        await using (var src = _fs.OpenRead(sourceFile))
        await using (var dst = _fs.OpenWrite(destFile))
        {
            // Best-effort preallocation: lets the OS/SMB server reserve space and
            // avoids file-system fragmentation on large files. Some shares disallow
            // SetLength on a fresh stream; ignore the failure and stream normally.
            try { dst.SetLength(totalBytes); } catch { /* some shares disallow */ }

            byte[] bufA = new byte[CopyBufferSize];
            byte[] bufB = new byte[CopyBufferSize];
            long totalRead = 0;

            int read = await src.ReadAsync(bufA.AsMemory(0, CopyBufferSize), token).ConfigureAwait(false);
            Task writeTask = Task.CompletedTask;

            while (read > 0)
            {
                token.ThrowIfCancellationRequested();
                // Honor pause between buffer chunks for snappier feel on huge files.
                await _pause.WaitWhilePausedAsync(token).ConfigureAwait(false);

                await writeTask.ConfigureAwait(false);
                writeTask = dst.WriteAsync(bufA.AsMemory(0, read), token).AsTask();

                totalRead += read;
                report(totalRead, totalBytes, false);

                (bufA, bufB) = (bufB, bufA);
                read = await src.ReadAsync(bufA.AsMemory(0, CopyBufferSize), token).ConfigureAwait(false);
            }
            await writeTask.ConfigureAwait(false);
            report(totalBytes, totalBytes, true);
        }

        // Preserve last-write time so SkipIfSame can recognize already-copied files later.
        try { _fs.SetLastWriteTimeUtc(destFile, _fs.GetLastWriteTimeUtc(sourceFile)); }
        catch (Exception ex) { Log.Debug(ex, "Could not preserve mtime on {Dest}", destFile); }

        if (options.DeleteSource)
        {
            _fs.DeleteFile(sourceFile);
        }

        return new OneFileResult(totalBytes, Skipped: false);
    }

    // ----------------------------- helpers -----------------------------

    private static bool FilesLookEquivalent(IFileSystem fs, string source, string dest)
    {
        try
        {
            long srcLen = fs.GetFileSize(source);
            long dstLen = fs.GetFileSize(dest);
            if (srcLen != dstLen) return false;
            var diff = (fs.GetLastWriteTimeUtc(source) - fs.GetLastWriteTimeUtc(dest)).Duration();
            return diff <= TimeSpan.FromSeconds(2);
        }
        catch
        {
            return false;
        }
    }

    private static string FindNonClashingName(IFileSystem fs, string path)
    {
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!fs.FileExists(candidate)) return candidate;
        }
        // Pathological — fall back to a timestamp.
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
    }
}
