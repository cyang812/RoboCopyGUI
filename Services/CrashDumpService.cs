using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>
/// Catches unhandled exceptions and writes a Windows minidump plus a readable
/// text sidecar to a <c>crashes/</c> folder next to the exe, capped at
/// <see cref="MaxDumps"/> dumps (LRU eviction). The intent is that a user
/// reporting a bug can attach a single small file from a known location.
/// </summary>
/// <remarks>
/// Wires into <see cref="AppDomain.UnhandledException"/> and
/// <see cref="TaskScheduler.UnobservedTaskException"/> per the Phase 6 spec.
/// Call <see cref="Install"/> exactly once at app startup. The service is
/// UI-framework-independent; the WinUI <c>App</c> layer additionally forwards
/// <c>Application.UnhandledException</c> to <see cref="WriteDump"/>.
/// </remarks>
public static class CrashDumpService
{
    /// <summary>Folder containing emitted .dmp + .txt files. Created on first write.</summary>
    public static string CrashDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "crashes");

    /// <summary>Maximum number of .dmp files retained. Older dumps are pruned LRU-style.</summary>
    public const int MaxDumps = 5;

    private static readonly object _writeLock = new();
    private static int _installed; // 0/1, mutated via Interlocked

    /// <summary>
    /// Install the global unhandled-exception hooks. Safe to call more than once;
    /// only the first call wires the hooks.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        try { Log.Information("Crash dump service installed. Directory: {Dir}", CrashDirectory); }
        catch { /* logger may not yet be initialized — fine */ }
    }

    /// <summary>
    /// Write a minidump + sidecar for <paramref name="ex"/>. Returns the dump
    /// path on success, or <c>null</c> if the minidump could not be written
    /// (sidecar may still have been written). Safe to call manually from
    /// framework-specific handlers (e.g. WinUI's <c>Application.UnhandledException</c>).
    /// </summary>
    public static string? WriteDump(Exception ex, string trigger = "manual", IFileSystem? fs = null) =>
        TryWriteDump(ex, trigger, fs);

    private static void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            TryWriteDump(ex, "AppDomain.UnhandledException");
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryWriteDump(e.Exception, "TaskScheduler.UnobservedTaskException");
        // Mark observed so a faulted background task doesn't tear down the app
        // — we've recorded the dump, the process can carry on.
        e.SetObserved();
    }

    private static string? TryWriteDump(Exception ex, string trigger, IFileSystem? fsArg = null)
    {
        // A handler MUST NOT throw — that would re-enter the unhandled exception
        // path or crash the process. Swallow + best-effort log everything.
        try
        {
            return WriteDumpInner(ex, trigger, fsArg ?? RealFileSystem.Instance);
        }
        catch (Exception writerEx)
        {
            try { Log.Error(writerEx, "Crash dump writer failed for {Trigger}", trigger); }
            catch { /* logger itself may be the casualty */ }
            return null;
        }
    }

    private static string? WriteDumpInner(Exception ex, string trigger, IFileSystem fs)
    {
        // Serialize concurrent crashes (e.g. an UnobservedTask firing while an
        // AppDomain handler is mid-write) so two writers don't race on the same
        // crashes/ directory + LRU prune.
        lock (_writeLock)
        {
            fs.CreateDirectory(CrashDirectory);

            DateTime now = DateTime.UtcNow;
            string dumpName = GenerateDumpFileName(now);
            string dumpPath = Path.Combine(CrashDirectory, dumpName);
            string sidecarPath = Path.ChangeExtension(dumpPath, ".txt")!;

            // 1) Native minidump. Production-only; tests don't exercise this path
            //    (no real process handle in xunit). It's best-effort — if dbghelp
            //    isn't available or the call fails, the .txt sidecar still gives
            //    the user something to attach to a bug report.
            bool dumpOk = TryWriteMinidump(dumpPath);

            // 2) Human-readable sidecar — written via the injected IFileSystem so
            //    tests can assert against it without the native dump.
            try
            {
                using var stream = fs.OpenWrite(sidecarPath);
                using var writer = new StreamWriter(stream);
                writer.WriteLine("RoboCopyGUI crash report");
                writer.WriteLine($"Trigger : {trigger}");
                writer.WriteLine($"UtcTime : {now:yyyy-MM-dd HH:mm:ss.fff}Z");
                writer.WriteLine($"Process : PID={Environment.ProcessId}, 64-bit={Environment.Is64BitProcess}");
                writer.WriteLine($"Dump    : {(dumpOk ? dumpName : "<failed>")}");
                writer.WriteLine();
                writer.WriteLine(ex.ToString());
            }
            catch { /* sidecar is best-effort */ }

            try { Log.Error(ex, "Crash dump written ({Trigger}): {Path}", trigger, dumpPath); }
            catch { /* logger may be down */ }

            PruneOldDumps(fs, CrashDirectory, MaxDumps);
            return dumpOk ? dumpPath : null;
        }
    }

    /// <summary>
    /// Generate the timestamped filename (no path) for a dump produced at the
    /// given UTC instant. Format: <c>crash-yyyyMMdd-HHmmss-ffff.dmp</c>.
    /// The 4-digit milliseconds suffix avoids same-second collisions when
    /// multiple handlers fire in quick succession.
    /// </summary>
    internal static string GenerateDumpFileName(DateTime utc) =>
        $"crash-{utc:yyyyMMdd-HHmmss-ffff}.dmp";

    /// <summary>
    /// Delete .dmp files (and their .txt sidecars) in <paramref name="dir"/>
    /// beyond the newest <paramref name="keep"/>. Sort key is last-write-time
    /// descending; a missing directory is a no-op.
    /// </summary>
    internal static void PruneOldDumps(IFileSystem fs, string dir, int keep)
    {
        if (keep < 0) throw new ArgumentOutOfRangeException(nameof(keep));
        if (!fs.DirectoryExists(dir)) return;

        var dumps = fs.EnumerateFiles(dir, recurse: false)
            .Where(p => Path.GetExtension(p).Equals(".dmp", StringComparison.OrdinalIgnoreCase))
            .Select(p => new { Path = p, Mtime = SafeMtime(fs, p) })
            .OrderByDescending(x => x.Mtime)
            .ToList();

        if (dumps.Count <= keep) return;

        foreach (var entry in dumps.Skip(keep))
        {
            try { fs.DeleteFile(entry.Path); }
            catch (Exception ex) { try { Log.Debug(ex, "Could not delete old dump {Path}", entry.Path); } catch { } }

            string sidecar = Path.ChangeExtension(entry.Path, ".txt")!;
            try { if (fs.FileExists(sidecar)) fs.DeleteFile(sidecar); }
            catch (Exception ex) { try { Log.Debug(ex, "Could not delete old sidecar {Path}", sidecar); } catch { } }
        }
    }

    private static DateTime SafeMtime(IFileSystem fs, string path)
    {
        try { return fs.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    // ---------- native minidump ----------

    [Flags]
    private enum MINIDUMP_TYPE : uint
    {
        Normal           = 0x00000000,
        WithDataSegs     = 0x00000001,
        WithHandleData   = 0x00000004,
        WithThreadInfo   = 0x00001000,
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeFileHandle hFile,
        MINIDUMP_TYPE dumpType,
        IntPtr expParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    private static bool TryWriteMinidump(string path)
    {
        // Use System.IO directly here (not IFileSystem) because we need the raw
        // SafeFileHandle for the P/Invoke; the abstraction is appropriate for
        // sidecar text where caller-substituted backends are useful.
        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var proc = Process.GetCurrentProcess();

            const MINIDUMP_TYPE type =
                MINIDUMP_TYPE.WithDataSegs |
                MINIDUMP_TYPE.WithHandleData |
                MINIDUMP_TYPE.WithThreadInfo;

            return MiniDumpWriteDump(
                proc.Handle, (uint)proc.Id,
                file.SafeFileHandle,
                type,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // dbghelp.dll absent, no permission, etc. The sidecar still gets written.
            try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
            return false;
        }
    }
}
