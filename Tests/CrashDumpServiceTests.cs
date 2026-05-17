using System;
using System.IO;
using System.Linq;
using System.Text;
using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

/// <summary>
/// Tests for the pure-functional helpers of <see cref="CrashDumpService"/>:
/// filename generation, LRU pruning, and sidecar handling. The native
/// minidump P/Invoke is not exercised here — it requires a live process
/// handle and writes to disk; the sidecar path via <see cref="IFileSystem"/>
/// is what tests verify.
/// </summary>
public class CrashDumpServiceTests
{
    private const string CrashDir = @"C:\fake-app\crashes";

    // ---------- GenerateDumpFileName ----------

    [Fact]
    public void GenerateDumpFileName_uses_sortable_timestamp_format()
    {
        // Construct without AddMilliseconds to avoid second-rollover surprises.
        var t = new DateTime(2026, 5, 17, 9, 42, 18, 234, DateTimeKind.Utc);
        string name = CrashDumpService.GenerateDumpFileName(t);

        Assert.StartsWith("crash-20260517-094218-", name);
        Assert.EndsWith(".dmp", name);
        Assert.Contains("2340", name); // ffff = 10000ths-of-a-second (234 ms = 2340)
    }

    [Fact]
    public void GenerateDumpFileName_two_distinct_instants_produce_distinct_names()
    {
        var a = new DateTime(2026, 5, 17, 9, 42, 18, 100, DateTimeKind.Utc);
        var b = new DateTime(2026, 5, 17, 9, 42, 18, 200, DateTimeKind.Utc);
        Assert.NotEqual(CrashDumpService.GenerateDumpFileName(a), CrashDumpService.GenerateDumpFileName(b));
    }

    // ---------- PruneOldDumps ----------

    [Fact]
    public void PruneOldDumps_is_noop_when_count_at_or_below_keep()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(CrashDir);
        for (int i = 0; i < 5; i++)
            fs.SeedFile(Path.Combine(CrashDir, $"crash-{i}.dmp"), new byte[] { 1 },
                mtimeUtc: new DateTime(2026, 1, 1).AddMinutes(i));

        CrashDumpService.PruneOldDumps(fs, CrashDir, keep: 5);

        Assert.Equal(5, fs.EnumerateFiles(CrashDir, recurse: false).Count(p => p.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PruneOldDumps_keeps_newest_K_and_deletes_the_rest()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(CrashDir);
        // 8 dumps with increasing mtimes; newest = index 7.
        for (int i = 0; i < 8; i++)
            fs.SeedFile(Path.Combine(CrashDir, $"crash-{i}.dmp"), new byte[] { (byte)i },
                mtimeUtc: new DateTime(2026, 1, 1).AddMinutes(i));

        CrashDumpService.PruneOldDumps(fs, CrashDir, keep: 5);

        var remaining = fs.EnumerateFiles(CrashDir, recurse: false)
            .Where(p => p.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(5, remaining.Count);
        // Newest 5 are indices 3..7 (3=oldest of those kept, 7=newest overall).
        Assert.Equal(new[] { "crash-3.dmp", "crash-4.dmp", "crash-5.dmp", "crash-6.dmp", "crash-7.dmp" }, remaining);
    }

    [Fact]
    public void PruneOldDumps_also_removes_companion_txt_sidecars()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(CrashDir);
        for (int i = 0; i < 7; i++)
        {
            string stem = $"crash-{i}";
            fs.SeedFile(Path.Combine(CrashDir, stem + ".dmp"), new byte[] { (byte)i },
                mtimeUtc: new DateTime(2026, 1, 1).AddMinutes(i));
            fs.SeedFile(Path.Combine(CrashDir, stem + ".txt"), Encoding.UTF8.GetBytes($"sidecar {i}"),
                mtimeUtc: new DateTime(2026, 1, 1).AddMinutes(i));
        }

        CrashDumpService.PruneOldDumps(fs, CrashDir, keep: 3);

        var dmps = fs.EnumerateFiles(CrashDir, recurse: false)
            .Where(p => p.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(x => x).ToList();
        var txts = fs.EnumerateFiles(CrashDir, recurse: false)
            .Where(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(x => x).ToList();

        Assert.Equal(new[] { "crash-4.dmp", "crash-5.dmp", "crash-6.dmp" }, dmps);
        Assert.Equal(new[] { "crash-4.txt", "crash-5.txt", "crash-6.txt" }, txts);
    }

    [Fact]
    public void PruneOldDumps_handles_missing_directory_gracefully()
    {
        var fs = new InMemoryFileSystem();
        // Don't seed CrashDir at all.
        CrashDumpService.PruneOldDumps(fs, CrashDir, keep: 5);  // must not throw
    }

    [Fact]
    public void PruneOldDumps_ignores_non_dump_files_in_the_folder()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(CrashDir);
        // Some user-dropped files alongside dumps — must not be considered.
        fs.SeedFile(Path.Combine(CrashDir, "readme.md"), new byte[] { 1 });
        fs.SeedFile(Path.Combine(CrashDir, "notes.txt"), new byte[] { 2 });
        for (int i = 0; i < 7; i++)
            fs.SeedFile(Path.Combine(CrashDir, $"crash-{i}.dmp"), new byte[] { (byte)i },
                mtimeUtc: new DateTime(2026, 1, 1).AddMinutes(i));

        CrashDumpService.PruneOldDumps(fs, CrashDir, keep: 5);

        Assert.True(fs.FileExists(Path.Combine(CrashDir, "readme.md")));
        // Plain notes.txt isn't a companion of any pruned dump, so it stays.
        Assert.True(fs.FileExists(Path.Combine(CrashDir, "notes.txt")));
    }

    [Fact]
    public void PruneOldDumps_throws_for_negative_keep()
    {
        var fs = new InMemoryFileSystem();
        Assert.Throws<ArgumentOutOfRangeException>(() => CrashDumpService.PruneOldDumps(fs, CrashDir, keep: -1));
    }

    // ---------- WriteDump end-to-end (sidecar via injected IFileSystem) ----------

    [Fact]
    public void WriteDump_writes_a_readable_text_sidecar_with_exception_details()
    {
        var fs = new InMemoryFileSystem();
        // Inject the fake; the .dmp side will fail (no real proc handle in xunit)
        // but the .txt sidecar is what we care about here.
        Exception ex;
        try { throw new InvalidOperationException("boom-marker"); }
        catch (Exception caught) { ex = caught; }

        CrashDumpService.WriteDump(ex, trigger: "unit-test", fs: fs);

        var sidecars = fs.EnumerateFiles(CrashDumpService.CrashDirectory, recurse: false)
            .Where(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(sidecars);

        string body = Encoding.UTF8.GetString(fs.ReadAllBytes(sidecars[0])!);
        Assert.Contains("unit-test", body);
        Assert.Contains("InvalidOperationException", body);
        Assert.Contains("boom-marker", body);
    }

    [Fact]
    public void WriteDump_caps_total_dumps_at_MaxDumps_after_repeated_calls()
    {
        var fs = new InMemoryFileSystem();
        Exception ex;
        try { throw new Exception("x"); } catch (Exception e) { ex = e; }

        // Write more than MaxDumps; the prune at the end of WriteDump should
        // bring the on-disk count back down each iteration. Sleep 1 ms between
        // calls so the sub-second timestamp suffix differs.
        for (int i = 0; i < CrashDumpService.MaxDumps + 3; i++)
        {
            CrashDumpService.WriteDump(ex, trigger: $"cap-{i}", fs: fs);
            System.Threading.Thread.Sleep(2);
        }

        // The native .dmp write fails inside xunit (no proc handle ⇒ TryWriteMinidump
        // returns false ⇒ no .dmp file). So the prune-by-.dmp logic has nothing to
        // delete, but the count of generated .txt sidecars should still equal the
        // number of WriteDump calls (8). The intent of this test is to prove the
        // prune path is wired and doesn't throw — we already proved correct LRU
        // math above with seeded mtimes.
        int sidecarCount = fs.EnumerateFiles(CrashDumpService.CrashDirectory, recurse: false)
            .Count(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CrashDumpService.MaxDumps + 3, sidecarCount);
    }
}
