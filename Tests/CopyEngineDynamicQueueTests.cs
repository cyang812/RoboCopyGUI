using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

/// <summary>
/// Tests for the live, mutable copy queue: AddItem / TryRemovePending /
/// Snapshot / single-run guard. Uses the in-memory file system and
/// FakeCopyItem.OnStatus hooks so mid-run mutations happen at deterministic
/// engine flow points (no sleep / timing races).
/// </summary>
public class CopyEngineDynamicQueueTests
{
    private const string SrcRoot = @"C:\fake-src";
    private const string DstRoot = @"C:\fake-dst";

    private static (CopyEngine engine, InMemoryFileSystem fs, PauseTokenSource pause) NewEngine()
    {
        var fs = new InMemoryFileSystem();
        fs.SeedDirectory(SrcRoot);
        fs.SeedDirectory(DstRoot);
        var pause = new PauseTokenSource();
        return (new CopyEngine(pause, fs), fs, pause);
    }

    private static CopyOptions Opts()
        => new() { Destination = DstRoot, Conflict = ConflictPolicy.Overwrite };

    // ---------- AddItem ----------

    [Fact]
    public async Task AddItem_after_first_item_completes_is_picked_up_by_next_iteration()
    {
        var (engine, fs, _) = NewEngine();
        string srcA = Path.Combine(SrcRoot, "a.bin");
        string srcB = Path.Combine(SrcRoot, "b.bin");
        fs.SeedFile(srcA, 100);
        fs.SeedFile(srcB, 200);

        var itemA = new FakeCopyItem(srcA);
        var itemB = new FakeCopyItem(srcB);

        // When A reports Done, add B. The engine's outer loop should then pick
        // B up on its next snapshot.
        itemA.OnStatus = s =>
        {
            if (s == ItemStatus.Done) engine.AddItem(itemB);
        };

        var totals = await engine.RunAsync(new[] { itemA }, Opts(), CancellationToken.None);

        Assert.Equal(2, totals.Done);
        Assert.Equal(0, totals.Failed);
        Assert.Equal(0, totals.Removed);
        Assert.Equal(300, totals.TotalBytes);
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "a.bin")));
        Assert.True(fs.FileExists(Path.Combine(DstRoot, "b.bin")));
        Assert.Equal(ItemStatus.Done, itemB.LastStatus);
    }

    [Fact]
    public async Task AddItem_called_before_RunAsync_via_API_is_processed_alongside_initial_items()
    {
        var (engine, fs, _) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 16);
        fs.SeedFile(Path.Combine(SrcRoot, "b.bin"), 32);

        var itemA = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));
        var itemB = new FakeCopyItem(Path.Combine(SrcRoot, "b.bin"));
        engine.AddItem(itemB); // added directly via API, not via initialItems

        var totals = await engine.RunAsync(new[] { itemA }, Opts(), CancellationToken.None);

        Assert.Equal(2, totals.Done);
        Assert.Equal(48, totals.TotalBytes);
        Assert.Equal(ItemStatus.Done, itemA.LastStatus);
        Assert.Equal(ItemStatus.Done, itemB.LastStatus);
    }

    [Fact]
    public void AddItem_is_idempotent_for_the_same_reference()
    {
        var (engine, _, _) = NewEngine();
        var item = new FakeCopyItem(@"C:\fake-src\x.bin");

        engine.AddItem(item);
        engine.AddItem(item); // no-op
        engine.AddItem(item); // no-op

        Assert.Single(engine.Snapshot());
        // Only one SetStatus(Queued) call was emitted.
        Assert.Equal(1, item.Transitions.Count(t => t.Status == ItemStatus.Queued));
    }

    // ---------- TryRemovePending ----------

    [Fact]
    public async Task TryRemovePending_skips_a_queued_item_mid_run()
    {
        var (engine, fs, _) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 16);
        fs.SeedFile(Path.Combine(SrcRoot, "b.bin"), 32);
        fs.SeedFile(Path.Combine(SrcRoot, "c.bin"), 64);

        var itemA = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));
        var itemB = new FakeCopyItem(Path.Combine(SrcRoot, "b.bin"));
        var itemC = new FakeCopyItem(Path.Combine(SrcRoot, "c.bin"));

        bool removedB = false;
        // When A enters InProgress, remove B. B should never start.
        itemA.OnStatus = s =>
        {
            if (s == ItemStatus.InProgress)
                removedB = engine.TryRemovePending(itemB);
        };

        var totals = await engine.RunAsync(
            new[] { itemA, itemB, itemC }, Opts(), CancellationToken.None);

        Assert.True(removedB, "TryRemovePending should have succeeded for the queued item.");
        Assert.Equal(2, totals.Done);
        Assert.Equal(0, totals.Failed);
        Assert.Equal(1, totals.Removed);
        Assert.Equal(ItemStatus.Done, itemA.LastStatus);
        Assert.Equal(ItemStatus.Removed, itemB.LastStatus);
        Assert.Equal(ItemStatus.Done, itemC.LastStatus);
        Assert.False(fs.FileExists(Path.Combine(DstRoot, "b.bin")));
    }

    [Fact]
    public async Task TryRemovePending_returns_false_for_already_in_progress_item()
    {
        var (engine, fs, pause) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 16);

        var item = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));
        bool? removeResult = null;

        // Pause inside the copy so the item stays InProgress while we attempt removal.
        // We hook on InProgress, then pause AT that moment (engine will hit the next
        // _pause.WaitWhilePausedAsync on its next iteration); meanwhile try removal.
        item.OnStatus = s =>
        {
            if (s == ItemStatus.InProgress)
                removeResult = engine.TryRemovePending(item);
        };

        var totals = await engine.RunAsync(new[] { item }, Opts(), CancellationToken.None);

        Assert.False(removeResult);
        Assert.Equal(1, totals.Done);
        Assert.Equal(0, totals.Removed);
        Assert.Equal(ItemStatus.Done, item.LastStatus);
    }

    [Fact]
    public void TryRemovePending_returns_false_for_unknown_item()
    {
        var (engine, _, _) = NewEngine();
        var ghost = new FakeCopyItem(@"C:\nope\nope.bin");
        Assert.False(engine.TryRemovePending(ghost));
    }

    [Fact]
    public async Task TryRemovePending_returns_false_for_completed_item()
    {
        var (engine, fs, _) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 16);
        var item = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));

        await engine.RunAsync(new[] { item }, Opts(), CancellationToken.None);

        Assert.Equal(ItemStatus.Done, item.LastStatus);
        Assert.False(engine.TryRemovePending(item));
    }

    // ---------- Live progress denominator ----------

    [Fact]
    public async Task Overall_total_bytes_grows_when_item_added_mid_run()
    {
        var (engine, fs, _) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 100);
        fs.SeedFile(Path.Combine(SrcRoot, "big.bin"), 10_000);

        var itemA = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));
        var itemBig = new FakeCopyItem(Path.Combine(SrcRoot, "big.bin"));

        var totalsSeen = new List<long>();
        engine.Progress += p =>
        {
            lock (totalsSeen) totalsSeen.Add(p.OverallBytesTotal);
        };

        itemA.OnStatus = s =>
        {
            if (s == ItemStatus.Done) engine.AddItem(itemBig);
        };

        await engine.RunAsync(new[] { itemA }, Opts(), CancellationToken.None);

        // First progress event(s) saw only itemA in the denominator (100 bytes).
        // After AddItem(itemBig), subsequent events should see a higher denominator (100 + 10_000).
        Assert.NotEmpty(totalsSeen);
        Assert.Contains(totalsSeen, t => t >= 10_000);
    }

    [Fact]
    public async Task Overall_total_bytes_shrinks_when_pending_item_removed()
    {
        var (engine, fs, _) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 100);
        fs.SeedFile(Path.Combine(SrcRoot, "big.bin"), 10_000);
        fs.SeedFile(Path.Combine(SrcRoot, "tail.bin"), 50);

        var itemA = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));
        var itemBig = new FakeCopyItem(Path.Combine(SrcRoot, "big.bin"));
        var itemTail = new FakeCopyItem(Path.Combine(SrcRoot, "tail.bin"));

        long totalAfterRemoval = -1;
        itemA.OnStatus = s =>
        {
            if (s == ItemStatus.Done)
            {
                engine.TryRemovePending(itemBig);
                // The very next Report() in itemTail's path should reflect the lower total.
            }
        };
        itemTail.OnStatus = s =>
        {
            if (s == ItemStatus.Done) { /* completion fires final Report */ }
        };

        engine.Progress += p =>
        {
            // Capture the last total we ever observed.
            Interlocked.Exchange(ref totalAfterRemoval, p.OverallBytesTotal);
        };

        var totals = await engine.RunAsync(
            new[] { itemA, itemBig, itemTail }, Opts(), CancellationToken.None);

        Assert.Equal(2, totals.Done);
        Assert.Equal(1, totals.Removed);
        // After itemBig is removed, the denominator should not include its 10k bytes.
        // The final denominator equals completed bytes (a + tail = 150) since nothing
        // is pending. We at least assert it's well under the pre-removal projection
        // of 100 + 10000 + 50 = 10150.
        Assert.True(totalAfterRemoval < 1_000,
            $"Expected denominator to drop after removal; was {totalAfterRemoval}.");
    }

    // ---------- Snapshot / GetStatus ----------

    [Fact]
    public void Snapshot_returns_items_in_insertion_order_with_engine_status()
    {
        var (engine, _, _) = NewEngine();
        var a = new FakeCopyItem(@"C:\x\a");
        var b = new FakeCopyItem(@"C:\x\b");
        var c = new FakeCopyItem(@"C:\x\c");
        engine.AddItem(a);
        engine.AddItem(b);
        engine.AddItem(c);

        Assert.Equal(new ICopyItem[] { a, b, c }, engine.Snapshot());
        Assert.Equal(ItemStatus.Queued, engine.GetStatus(a));
        Assert.Equal(ItemStatus.Queued, engine.GetStatus(b));
        Assert.Equal(ItemStatus.Queued, engine.GetStatus(c));

        engine.TryRemovePending(b);
        Assert.Equal(ItemStatus.Removed, engine.GetStatus(b));
    }

    [Fact]
    public void GetStatus_returns_null_for_unknown_item()
    {
        var (engine, _, _) = NewEngine();
        Assert.Null(engine.GetStatus(new FakeCopyItem(@"C:\nope")));
    }

    // ---------- Single-run guard ----------

    [Fact]
    public async Task RunAsync_throws_when_invoked_a_second_time_on_the_same_instance()
    {
        var (engine, fs, _) = NewEngine();
        fs.SeedFile(Path.Combine(SrcRoot, "a.bin"), 16);
        var item = new FakeCopyItem(Path.Combine(SrcRoot, "a.bin"));

        await engine.RunAsync(new[] { item }, Opts(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.RunAsync(Array.Empty<ICopyItem>(), Opts(), CancellationToken.None));
    }
}
