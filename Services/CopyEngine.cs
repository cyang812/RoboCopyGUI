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
public sealed class CopyEngine
{
    public const int CopyBufferSize = 1 * 1024 * 1024;
    private const int ProgressUpdateIntervalMs = 150;

    private readonly PauseTokenSource _pause;

    public CopyEngine(PauseTokenSource pause) => _pause = pause;

    /// <summary>Fired (throttled) while a file is being copied.</summary>
    public event Action<CopyProgress>? Progress;

    /// <summary>
    /// Walk the queued items and total their sizes. Cheap for files; for directories
    /// it recursively sums file lengths. Safe to call before <see cref="RunAsync"/>.
    /// Errors enumerating a particular item are swallowed (counted as 0) so the
    /// preflight never blocks a copy from starting.
    /// </summary>
    public static long Preflight(IEnumerable<ICopyItem> items, CancellationToken token = default)
    {
        long total = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(item.Path))
                {
                    foreach (var f in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        try { total += new FileInfo(f).Length; }
                        catch { /* unreadable file — skip */ }
                    }
                }
                else if (File.Exists(item.Path))
                {
                    total += new FileInfo(item.Path).Length;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Preflight could not size {Path}", item.Path);
            }
        }
        return total;
    }

    public async Task<CopyTotals> RunAsync(
        IList<ICopyItem> items,
        CopyOptions options,
        CancellationToken token)
    {
        var totals = new CopyTotals();
        if (items.Count == 0) return totals;

        long totalQueueBytes = Preflight(items, token);
        var overall = Stopwatch.StartNew();

        // Throttled, rolling speed counter shared across all files in this run.
        // overallBytes is mutated from multiple worker tasks during parallel small-file
        // copies, so we touch it through Interlocked / the gate lock below.
        var lastTick = Stopwatch.StartNew();
        long bytesAtLastTick = 0;
        long overallBytes = 0;
        string currentName = string.Empty;
        double smoothedMBps = 0;
        var reportLock = new object();

        void Report(long copied, long total, bool force)
        {
            lock (reportLock)
            {
                if (!force && lastTick.ElapsedMilliseconds < ProgressUpdateIntervalMs) return;

                long ms = Math.Max(1, lastTick.ElapsedMilliseconds);
                long copiedOverall = Interlocked.Read(ref overallBytes) + copied;
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

                Progress?.Invoke(new CopyProgress(
                    currentName,
                    copied, total,
                    mbps,
                    copiedOverall, totalQueueBytes,
                    eta));
            }
        }

        // Split the top-level queue into "small" files (eligible for parallel copy)
        // and everything else (directories + large files). Large files keep the
        // sequential, pipelined path so the network pipe isn't fragmented.
        bool parallelEnabled = options.MaxParallelSmallFiles > 1;
        var smallTopLevel = new List<ICopyItem>();
        var sequentialTopLevel = new List<ICopyItem>();
        if (parallelEnabled)
        {
            foreach (var item in items)
            {
                if (File.Exists(item.Path))
                {
                    long len;
                    try { len = new FileInfo(item.Path).Length; }
                    catch { len = long.MaxValue; }
                    if (len <= options.SmallFileThresholdBytes)
                    {
                        smallTopLevel.Add(item);
                        continue;
                    }
                }
                sequentialTopLevel.Add(item);
            }
        }
        else
        {
            sequentialTopLevel.AddRange(items);
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
                        Interlocked.Add(ref overallBytes, r.Bytes);
                        lock (reportLock) { totals.TotalBytes += r.Bytes; if (r.Skipped) totals.Skipped++; else totals.Done++; }
                        item.SetStatus(r.Skipped ? ItemStatus.Skipped : ItemStatus.Done);
                        Report(0, 0, force: true);
                    }
                    catch (OperationCanceledException)
                    {
                        item.SetStatus(ItemStatus.Queued);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lock (reportLock) totals.Failed++;
                        item.SetStatus(ItemStatus.Failed, ex.Message);
                        Log.Error(ex, "Failed to copy {Path}", item.Path);
                    }
                }).ConfigureAwait(false);
        }

        // Phase B: sequential pass over directories + large files. This is the original
        // pipelined path; inside a directory we may still parallelize *small* files
        // belonging to that tree (CopyDirectoryAsync handles that internally).
        foreach (var item in sequentialTopLevel)
        {
            token.ThrowIfCancellationRequested();
            await _pause.WaitWhilePausedAsync(token).ConfigureAwait(false);

            item.SetStatus(ItemStatus.InProgress);
            currentName = Path.GetFileName(item.Path);

            try
            {
                long bytes;
                bool skippedTopLevel = false;

                if (Directory.Exists(item.Path))
                {
                    string destDir = Path.Combine(options.Destination, Path.GetFileName(item.Path));
                    bytes = await CopyDirectoryAsync(item.Path, destDir, options, token,
                        setName: n => { lock (reportLock) currentName = n; },
                        report: Report,
                        addOverall: b => Interlocked.Add(ref overallBytes, b)).ConfigureAwait(false);
                    // CopyDirectoryAsync already called addOverall for every file copied,
                    // so DO NOT add `bytes` to overallBytes again below.

                    if (options.DeleteSource)
                    {
                        try { Directory.Delete(item.Path, recursive: true); }
                        catch (Exception ex) { Log.Warning(ex, "Could not delete source dir {Path}", item.Path); }
                    }
                }
                else if (File.Exists(item.Path))
                {
                    string destFile = Path.Combine(options.Destination, Path.GetFileName(item.Path));
                    var fileResult = await CopyOneFileAsync(item.Path, destFile, options, token, Report).ConfigureAwait(false);
                    bytes = fileResult.Bytes;
                    skippedTopLevel = fileResult.Skipped;
                    Interlocked.Add(ref overallBytes, bytes);
                }
                else
                {
                    throw new FileNotFoundException("Source no longer exists", item.Path);
                }

                totals.TotalBytes += bytes;

                if (skippedTopLevel)
                {
                    totals.Skipped++;
                    item.SetStatus(ItemStatus.Skipped);
                    Log.Information("Skipped (existing): {Path}", item.Path);
                }
                else
                {
                    totals.Done++;
                    item.SetStatus(ItemStatus.Done);
                }
            }
            catch (OperationCanceledException)
            {
                item.SetStatus(ItemStatus.Queued);
                throw;
            }
            catch (Exception ex)
            {
                totals.Failed++;
                item.SetStatus(ItemStatus.Failed, ex.Message);
                Log.Error(ex, "Failed to copy {Path}", item.Path);
                // Keep going to next item — per-file resilience.
            }
        }

        overall.Stop();
        totals.Elapsed = overall.Elapsed;
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
        Action<long> addOverall)
    {
        Directory.CreateDirectory(destDir);
        long bytes = 0;

        // Split this folder's files: small ones go through the parallel path,
        // big ones stream sequentially with full per-buffer progress reporting.
        bool parallelEnabled = options.MaxParallelSmallFiles > 1;
        var smallFiles = new List<string>();
        var largeFiles = new List<string>();
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            token.ThrowIfCancellationRequested();
            long len;
            try { len = new FileInfo(file).Length; }
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
                            // we account for the bytes at the end via addOverall.
                            (_, _, _) => { }).ConfigureAwait(false);
                        Interlocked.Add(ref bytes, r.Bytes);
                        addOverall(r.Bytes);
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
                addOverall(result.Bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Per-file error inside a directory tree: log and continue with siblings.
                Log.Warning(ex, "Skipping file inside tree: {File}", file);
            }
        }

        foreach (string folder in Directory.EnumerateDirectories(sourceDir))
        {
            token.ThrowIfCancellationRequested();
            long sub = await CopyDirectoryAsync(
                folder,
                Path.Combine(destDir, Path.GetFileName(folder)),
                options, token, setName, report, addOverall).ConfigureAwait(false);
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
        if (File.Exists(destFile))
        {
            switch (options.Conflict)
            {
                case ConflictPolicy.Skip:
                    return new OneFileResult(0, Skipped: true);

                case ConflictPolicy.SkipIfSame:
                    if (FilesLookEquivalent(sourceFile, destFile))
                        return new OneFileResult(0, Skipped: true);
                    break;

                case ConflictPolicy.Rename:
                    destFile = FindNonClashingName(destFile);
                    break;

                case ConflictPolicy.Overwrite:
                default:
                    break; // FileMode.Create below will overwrite.
            }
        }

        long totalBytes = new FileInfo(sourceFile).Length;

        // Tuning notes:
        //   - 1 MiB buffer amortizes SMB packet overhead.
        //   - FileOptions.Asynchronous: enables overlapped I/O at the Win32 layer.
        //   - FileOptions.SequentialScan: hint to the OS read-cache, helps spinning HDDs.
        //   - We deliberately do NOT use FileOptions.WriteThrough — it disables the SMB
        //     server's write cache and tanks throughput on home NAS.
        const FileOptions readOpts = FileOptions.Asynchronous | FileOptions.SequentialScan;
        const FileOptions writeOpts = FileOptions.Asynchronous;

        await using (var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
                         FileShare.Read, CopyBufferSize, readOpts))
        await using (var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write,
                         FileShare.None, CopyBufferSize, writeOpts))
        {
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
        try { File.SetLastWriteTimeUtc(destFile, File.GetLastWriteTimeUtc(sourceFile)); }
        catch (Exception ex) { Log.Debug(ex, "Could not preserve mtime on {Dest}", destFile); }

        if (options.DeleteSource)
        {
            File.Delete(sourceFile);
        }

        return new OneFileResult(totalBytes, Skipped: false);
    }

    // ----------------------------- helpers -----------------------------

    private static bool FilesLookEquivalent(string source, string dest)
    {
        try
        {
            var s = new FileInfo(source);
            var d = new FileInfo(dest);
            if (s.Length != d.Length) return false;
            var diff = (s.LastWriteTimeUtc - d.LastWriteTimeUtc).Duration();
            return diff <= TimeSpan.FromSeconds(2);
        }
        catch
        {
            return false;
        }
    }

    private static string FindNonClashingName(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // Pathological — fall back to a timestamp.
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
    }
}
