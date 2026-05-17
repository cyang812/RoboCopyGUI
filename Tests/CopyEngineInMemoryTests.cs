using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

/// <summary>
/// Re-runs the key <see cref="CopyEngine"/> behaviors against the in-memory
/// <see cref="InMemoryFileSystem"/> fake to prove the <see cref="IFileSystem"/>
/// abstraction is truly substitutable (no hidden real-disk calls).
/// Also covers a handful of fake-specific contract sanity checks.
/// </summary>
public class CopyEngineInMemoryTests
{
    // Use rooted, Windows-style paths so InMemoryFileSystem.Normalize() doesn't
    // anchor onto whatever cwd the test host happens to be in.
    private const string SrcRoot = @"C:\fake-src";
    private const string DstRoot = @"C:\fake-dst";

    private static (CopyEngine engine, InMemoryFileSystem fs) NewEngine()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(SrcRoot);
        fs.SeedDirectory(DstRoot);
        return (new CopyEngine(new PauseTokenSource(), fs), fs);
    }

    private static CopyOptions Opts(ConflictPolicy conflict = ConflictPolicy.Overwrite,
                                    bool deleteSource = false,
                                    int parallel = 1)
        => new()
        {
            Destination = DstRoot,
            Conflict = conflict,
            DeleteSource = deleteSource,
            MaxParallelSmallFiles = parallel,
            SmallFileThresholdBytes = 1 * 1024 * 1024,
        };

    [Fact]
    public async Task Copies_single_file_and_marks_done()
    {
        var (engine, fs) = NewEngine();
        string src = Path.Combine(SrcRoot, "a.bin");
        fs.SeedFile(src, size: 16 * 1024);

        var item = new FakeCopyItem(src);
        var totals = await engine.RunAsync(new[] { item }, Opts(), CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.Equal(0, totals.Failed);
        Assert.Equal(16 * 1024, totals.TotalBytes);
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "a.bin")));
        Assert.Equal(16 * 1024, fs.ReadAllBytes(Path.Combine(DstRoot, "a.bin"))!.Length);
        Assert.Equal(ItemStatus.Done, item.LastStatus);
    }

    [Fact]
    public async Task Copies_directory_recursively()
    {
        var (engine, fs) = NewEngine();
        string treeRoot = Path.Combine(SrcRoot, "tree");
        fs.SeedFile(Path.Combine(treeRoot, "root.txt"), 32);
        fs.SeedFile(Path.Combine(treeRoot, "sub", "child.txt"), 64);
        fs.SeedFile(Path.Combine(treeRoot, "sub", "deep", "leaf.bin"), 128);

        var item = new FakeCopyItem(treeRoot);
        var totals = await engine.RunAsync(new[] { item }, Opts(), CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.Equal(32 + 64 + 128, totals.TotalBytes);
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "tree", "root.txt")));
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "tree", "sub", "child.txt")));
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "tree", "sub", "deep", "leaf.bin")));
    }

    [Fact]
    public async Task Move_mode_deletes_source_file()
    {
        var (engine, fs) = NewEngine();
        string src = Path.Combine(SrcRoot, "move-me.bin");
        fs.SeedFile(src, 4096);

        var item = new FakeCopyItem(src);
        var totals = await engine.RunAsync(new[] { item }, Opts(deleteSource: true), CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.False(fs.FileExists(src));
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "move-me.bin")));
    }

    [Fact]
    public async Task Move_mode_deletes_source_directory_tree()
    {
        var (engine, fs) = NewEngine();
        string treeRoot = Path.Combine(SrcRoot, "tree");
        fs.SeedFile(Path.Combine(treeRoot, "a.txt"), 100);
        fs.SeedFile(Path.Combine(treeRoot, "sub", "b.txt"), 200);

        var item = new FakeCopyItem(treeRoot);
        await engine.RunAsync(new[] { item }, Opts(deleteSource: true), CancellationToken.None);

        Assert.False(fs.DirectoryExists(treeRoot));
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "tree", "a.txt")));
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "tree", "sub", "b.txt")));
    }

    [Fact]
    public async Task Conflict_policy_skip_keeps_existing_destination()
    {
        var (engine, fs) = NewEngine();
        string src = Path.Combine(SrcRoot, "c.bin");
        string dst = Path.Combine(DstRoot, "c.bin");
        fs.SeedFile(src, new byte[] { 1, 2, 3 });
        fs.SeedFile(dst, new byte[] { 9, 9, 9 });

        var item = new FakeCopyItem(src);
        var totals = await engine.RunAsync(new[] { item }, Opts(ConflictPolicy.Skip), CancellationToken.None);

        Assert.Equal(0, totals.Done);
        Assert.Equal(1, totals.Skipped);
        Assert.Equal(new byte[] { 9, 9, 9 }, fs.ReadAllBytes(dst));
        Assert.Equal(ItemStatus.Skipped, item.LastStatus);
    }

    [Fact]
    public async Task Conflict_policy_overwrite_replaces_destination()
    {
        var (engine, fs) = NewEngine();
        string src = Path.Combine(SrcRoot, "o.bin");
        string dst = Path.Combine(DstRoot, "o.bin");
        fs.SeedFile(src, new byte[] { 1, 2, 3, 4 });
        fs.SeedFile(dst, new byte[] { 9, 9, 9 });

        var item = new FakeCopyItem(src);
        var totals = await engine.RunAsync(new[] { item }, Opts(ConflictPolicy.Overwrite), CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, fs.ReadAllBytes(dst));
    }

    [Fact]
    public async Task Conflict_policy_rename_creates_numbered_sibling()
    {
        var (engine, fs) = NewEngine();
        string src = Path.Combine(SrcRoot, "name.txt");
        fs.SeedFile(src, new byte[] { 1, 2 });
        fs.SeedFile(Path.Combine(DstRoot, "name.txt"), new byte[] { 9 });

        var item = new FakeCopyItem(src);
        var totals = await engine.RunAsync(new[] { item }, Opts(ConflictPolicy.Rename), CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "name.txt")));
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "name (1).txt")));
        Assert.Equal(new byte[] { 1, 2 }, fs.ReadAllBytes(Path.Combine(DstRoot, "name (1).txt")));
    }

    [Fact]
    public async Task Conflict_policy_skip_if_same_skips_when_size_and_mtime_match()
    {
        var (engine, fs) = NewEngine();
        var ts = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string src = Path.Combine(SrcRoot, "s.bin");
        string dst = Path.Combine(DstRoot, "s.bin");
        fs.SeedFile(src, new byte[] { 1, 2, 3, 4 }, mtimeUtc: ts);
        fs.SeedFile(dst, new byte[] { 1, 2, 3, 4 }, mtimeUtc: ts);

        var item = new FakeCopyItem(src);
        var totals = await engine.RunAsync(new[] { item }, Opts(ConflictPolicy.SkipIfSame), CancellationToken.None);

        Assert.Equal(0, totals.Done);
        Assert.Equal(1, totals.Skipped);
    }

    [Fact]
    public async Task Failed_item_does_not_abort_remaining_items()
    {
        var (engine, fs) = NewEngine();
        string ok1 = Path.Combine(SrcRoot, "ok1.bin");
        string ghost = Path.Combine(SrcRoot, "ghost.bin"); // never seeded
        string ok2 = Path.Combine(SrcRoot, "ok2.bin");
        fs.SeedFile(ok1, 16);
        fs.SeedFile(ok2, 32);

        var items = new[] { new FakeCopyItem(ok1), new FakeCopyItem(ghost), new FakeCopyItem(ok2) };
        var totals = await engine.RunAsync(items, Opts(), CancellationToken.None);

        Assert.Equal(2, totals.Done);
        Assert.Equal(1, totals.Failed);
        Assert.Equal(ItemStatus.Done, items[0].LastStatus);
        Assert.Equal(ItemStatus.Failed, items[1].LastStatus);
        Assert.Equal(ItemStatus.Done, items[2].LastStatus);
    }

    [Fact]
    public async Task Cancellation_before_run_throws_OperationCanceled()
    {
        var (engine, fs) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 1024);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var item = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.RunAsync(new[] { item }, Opts(), cts.Token));
    }

    [Fact]
    public void Preflight_totals_files_and_directories_via_injected_fs()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 100);
        fs.SeedFile(Path.Combine(SrcRoot, "dir", "b.bin"), 250);
        fs.SeedFile(Path.Combine(SrcRoot, "dir", "sub", "c.bin"), 400);

        var items = new ICopyItem[]
        {
            new FakeCopyItem(Path.Combine(SrcRoot, "a.bin")),
            new FakeCopyItem(Path.Combine(SrcRoot, "dir")),
        };

        long total = CopyEngine.Preflight(items, fs);
        Assert.Equal(100 + 250 + 400, total);
    }

    [Fact]
    public async Task Parallel_small_file_copies_complete_all_files()
    {
        var (engine, fs) = NewEngine();
        for (int i = 0; i < 24; i++)
            fs.SeedFile(Path.Combine(SrcRoot, $"small-{i:D2}.bin"), 4096, fill: (byte)i);

        var items = fs.EnumerateFiles(SrcRoot, recurse: false)
            .Select(p => new FakeCopyItem(p))
            .ToArray();

        var totals = await engine.RunAsync(items, Opts(parallel: 4), CancellationToken.None);

        Assert.Equal(24, totals.Done);
        Assert.Equal(24 * 4096L, totals.TotalBytes);
        Assert.Equal(24, fs.EnumerateFiles(DstRoot, recurse: false).Count());
        foreach (var item in items) Assert.Equal(ItemStatus.Done, item.LastStatus);
    }

    // ---------- Fake-specific contract checks ----------
    // These guard the in-memory fake itself so regressions in the abstraction
    // boundary (especially around OpenWrite-time reservation and parent-dir
    // enforcement, called out by the rubber-duck critique) are caught early.

    [Fact]
    public void OpenWrite_reserves_destination_immediately_for_subsequent_FileExists_checks()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(DstRoot);
        string dst = Path.Combine(DstRoot, "reserved.bin");

        Assert.False(fs.FileExists(dst));
        var stream = fs.OpenWrite(dst);
        try { Assert.True(fs.FileExists(dst)); }
        finally { stream.Dispose(); }
        Assert.True(fs.FileExists(dst));
    }

    [Fact]
    public void OpenWrite_refuses_concurrent_writer_for_same_path()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(DstRoot);
        string dst = Path.Combine(DstRoot, "single-writer.bin");

        using var first = fs.OpenWrite(dst);
        Assert.Throws<IOException>(() => fs.OpenWrite(dst));
    }

    [Fact]
    public void OpenWrite_throws_when_parent_directory_missing()
    {
        var fs = new InMemoryFileSystem();
        // Deliberately do NOT seed C:\nope-dir
        Assert.Throws<DirectoryNotFoundException>(() => fs.OpenWrite(@"C:\nope-dir\file.bin"));
    }

    [Fact]
    public void DeleteDirectory_non_recursive_throws_when_not_empty()
    {
        var fs = new InMemoryFileSystem();
        string dir = Path.Combine(SrcRoot, "filled");
        fs.SeedFile(Path.Combine(dir, "f.txt"), new byte[] { 1 });

        Assert.Throws<IOException>(() => fs.DeleteDirectory(dir, recursive: false));
    }

    [Fact]
    public void DeleteFile_missing_path_is_noop()
    {
        var fs = new InMemoryFileSystem();
        // Mirrors File.Delete behavior — no exception for a missing file.
        fs.DeleteFile(@"C:\fake-src\does-not-exist.bin");
    }
}
