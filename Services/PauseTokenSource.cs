using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoboCopyGUI.Services;

/// <summary>
/// Lightweight pause/resume primitive. Awaiters call <see cref="WaitWhilePausedAsync"/>
/// at safe points; <see cref="Pause"/>/<see cref="Resume"/> toggle the gate. Cancellation
/// is honored while waiting for the resume.
/// </summary>
public sealed class PauseTokenSource
{
    private readonly object _lock = new();
    private TaskCompletionSource _tcs = CreateCompleted();
    private bool _isPaused;

    public bool IsPaused
    {
        get { lock (_lock) { return _isPaused; } }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_isPaused) return;
            _isPaused = true;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? toComplete = null;
        lock (_lock)
        {
            if (!_isPaused) return;
            _isPaused = false;
            toComplete = _tcs;
        }
        toComplete.TrySetResult();
    }

    /// <summary>
    /// Returns a completed task when not paused. When paused, returns a task that
    /// completes when <see cref="Resume"/> is called or throws if <paramref name="ct"/>
    /// is cancelled first.
    /// </summary>
    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task gate;
        lock (_lock)
        {
            if (!_isPaused) return Task.CompletedTask;
            gate = _tcs.Task;
        }
        return WaitWithCancellation(gate, ct);
    }

    private static async Task WaitWithCancellation(Task gate, CancellationToken ct)
    {
        if (gate.IsCompleted) { ct.ThrowIfCancellationRequested(); return; }

        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), cancelTcs))
        {
            await Task.WhenAny(gate, cancelTcs.Task).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource CreateCompleted()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
