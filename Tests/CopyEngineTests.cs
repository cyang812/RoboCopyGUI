using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

public class CopyEngineTests
{
    private static CopyEngine NewEngine() => new(new PauseTokenSource());

    [Fact]
    public async Task Copies_single_file_and_marks_done()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string file = src.MakeFile("a.bin", 16 * 1024);

        var item = new FakeCopyItem(file);
        var totals = await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.Overwrite },
            CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.Equal(0, totals.Failed);
        Assert.Equal(0, totals.Skipped);
        Assert.Equal(16 * 1024, totals.TotalBytes);
        Assert.True(File.Exists(Path.Combine(dst.Root, "a.bin")));
        Assert.Contains((ItemStatus.InProgress, null), item.Transitions);
        Assert.Equal(ItemStatus.Done, item.LastStatus);
    }

    [Fact]
    public async Task Copies_directory_recursively()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.MakeFile(Path.Combine("tree", "root.txt"), 32);
        src.MakeFile(Path.Combine("tree", "sub", "child.txt"), 64);
        src.MakeFile(Path.Combine("tree", "sub", "deep", "leaf.bin"), 128);

        var item = new FakeCopyItem(Path.Combine(src.Root, "tree"));
        var totals = await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.Overwrite },
            CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.Equal(32 + 64 + 128, totals.TotalBytes);
        Assert.True(File.Exists(Path.Combine(dst.Root, "tree", "root.txt")));
        Assert.True(File.Exists(Path.Combine(dst.Root, "tree", "sub", "child.txt")));
        Assert.True(File.Exists(Path.Combine(dst.Root, "tree", "sub", "deep", "leaf.bin")));
    }

    [Fact]
    public async Task Move_mode_deletes_source_files()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string file = src.MakeFile("move-me.bin", 4096);

        var item = new FakeCopyItem(file);
        var totals = await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, DeleteSource = true, Conflict = ConflictPolicy.Overwrite },
            CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(dst.Root, "move-me.bin")));
    }

    [Fact]
    public async Task Move_mode_deletes_source_directory_tree()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string root = src.MakeDir("tree");
        src.MakeFile(Path.Combine("tree", "a.txt"), 100);
        src.MakeFile(Path.Combine("tree", "sub", "b.txt"), 200);

        var item = new FakeCopyItem(root);
        await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, DeleteSource = true, Conflict = ConflictPolicy.Overwrite },
            CancellationToken.None);

        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(dst.Root, "tree", "a.txt")));
        Assert.True(File.Exists(Path.Combine(dst.Root, "tree", "sub", "b.txt")));
    }

    [Fact]
    public async Task Conflict_policy_skip_keeps_existing_destination()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string srcFile = src.MakeFile("c.bin", new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(dst.Root, "c.bin"), new byte[] { 9, 9, 9 });

        var item = new FakeCopyItem(srcFile);
        var totals = await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.Skip },
            CancellationToken.None);

        Assert.Equal(0, totals.Done);
        Assert.Equal(1, totals.Skipped);
        Assert.Equal(ItemStatus.Skipped, item.LastStatus);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(dst.Root, "c.bin")));
    }

    [Fact]
    public async Task Conflict_policy_overwrite_replaces_destination()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string srcFile = src.MakeFile("o.bin", new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(dst.Root, "o.bin"), new byte[] { 9, 9, 9 });

        var item = new FakeCopyItem(srcFile);
        var totals = await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.Overwrite },
            CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(dst.Root, "o.bin")));
    }

    [Fact]
    public async Task Conflict_policy_rename_creates_numbered_sibling()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string srcFile = src.MakeFile("name.txt", new byte[] { 1, 2 });
        File.WriteAllBytes(Path.Combine(dst.Root, "name.txt"), new byte[] { 9 });

        var item = new FakeCopyItem(srcFile);
        var totals = await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.Rename },
            CancellationToken.None);

        Assert.Equal(1, totals.Done);
        Assert.True(File.Exists(Path.Combine(dst.Root, "name.txt")));
        Assert.True(File.Exists(Path.Combine(dst.Root, "name (1).txt")));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(dst.Root, "name (1).txt")));
    }

    [Fact]
    public async Task Conflict_policy_skip_if_same_skips_when_size_and_mtime_match()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string srcFile = src.MakeFile("s.bin", new byte[] { 1, 2, 3, 4 });
        string dstFile = Path.Combine(dst.Root, "s.bin");
        File.WriteAllBytes(dstFile, new byte[] { 1, 2, 3, 4 });
        var ts = File.GetLastWriteTimeUtc(srcFile);
        File.SetLastWriteTimeUtc(dstFile, ts);

        var item = new FakeCopyItem(srcFile);
        var totals = await NewEngine().RunAsync(
            new[] { item },
            new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.SkipIfSame },
            CancellationToken.None);

        Assert.Equal(0, totals.Done);
        Assert.Equal(1, totals.Skipped);
    }

    [Fact]
    public async Task Failed_item_does_not_abort_remaining_items()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string ok1 = src.MakeFile("ok1.bin", 16);
        string ghost = Path.Combine(src.Root, "ghost.bin"); // never created -> FileNotFoundException
        string ok2 = src.MakeFile("ok2.bin", 32);

        var items = new[] { new FakeCopyItem(ok1), new FakeCopyItem(ghost), new FakeCopyItem(ok2) };
        var totals = await NewEngine().RunAsync(
            items,
            new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.Overwrite },
            CancellationToken.None);

        Assert.Equal(2, totals.Done);
        Assert.Equal(1, totals.Failed);
        Assert.Equal(ItemStatus.Done, items[0].LastStatus);
        Assert.Equal(ItemStatus.Failed, items[1].LastStatus);
        Assert.Equal(ItemStatus.Done, items[2].LastStatus);
        Assert.True(File.Exists(Path.Combine(dst.Root, "ok1.bin")));
        Assert.True(File.Exists(Path.Combine(dst.Root, "ok2.bin")));
    }

    [Fact]
    public async Task Cancellation_before_run_throws_OperationCanceled()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        string file = src.MakeFile("a.bin", 1024);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled

        var item = new FakeCopyItem(file);
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(async () =>
            await NewEngine().RunAsync(
                new[] { item },
                new CopyOptions { Destination = dst.Root, Conflict = ConflictPolicy.Overwrite },
                cts.Token));
    }

    [Fact]
    public void Preflight_totals_files_and_directories()
    {
        using var src = new TempDir();
        src.MakeFile("a.bin", 100);
        src.MakeFile(Path.Combine("dir", "b.bin"), 250);
        src.MakeFile(Path.Combine("dir", "sub", "c.bin"), 400);

        var items = new ICopyItem[]
        {
            new FakeCopyItem(Path.Combine(src.Root, "a.bin")),
            new FakeCopyItem(Path.Combine(src.Root, "dir")),
        };

        long total = CopyEngine.Preflight(items);
        Assert.Equal(100 + 250 + 400, total);
    }

    [Fact]
    public async Task Parallel_small_file_copies_complete_all_files()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        for (int i = 0; i < 24; i++) src.MakeFile($"small-{i:D2}.bin", 4096, fill: (byte)i);

        var items = Directory.EnumerateFiles(src.Root)
            .Select(p => new FakeCopyItem(p))
            .ToArray();

        var totals = await NewEngine().RunAsync(
            items,
            new CopyOptions
            {
                Destination = dst.Root,
                Conflict = ConflictPolicy.Overwrite,
                MaxParallelSmallFiles = 4,
                SmallFileThresholdBytes = 1 * 1024 * 1024,
            },
            CancellationToken.None);

        Assert.Equal(24, totals.Done);
        Assert.Equal(24 * 4096L, totals.TotalBytes);
        Assert.Equal(24, Directory.GetFiles(dst.Root).Length);
        foreach (var item in items) Assert.Equal(ItemStatus.Done, item.LastStatus);
    }
}
