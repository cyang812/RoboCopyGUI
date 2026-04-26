using System.Threading;
using System.Threading.Tasks;
using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

public class PauseTokenSourceTests
{
    [Fact]
    public async Task Resume_releases_waiters()
    {
        var p = new PauseTokenSource();
        p.Pause();
        Assert.True(p.IsPaused);

        var waiter = p.WaitWhilePausedAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        p.Resume();
        // Should complete immediately once resumed.
        await waiter.WaitAsync(System.TimeSpan.FromSeconds(2));
        Assert.False(p.IsPaused);
    }

    [Fact]
    public async Task Cancel_during_pause_throws()
    {
        var p = new PauseTokenSource();
        p.Pause();
        using var cts = new CancellationTokenSource();
        var waiter = p.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(async () => await waiter);
    }

    [Fact]
    public async Task Not_paused_returns_completed_task()
    {
        var p = new PauseTokenSource();
        await p.WaitWhilePausedAsync(CancellationToken.None);
        Assert.False(p.IsPaused);
    }
}
