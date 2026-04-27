using System;
using System.Runtime.InteropServices;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>
/// Wraps Win32 SetThreadExecutionState so a long-running copy can keep the
/// machine awake (and the display on) until it finishes.
/// </summary>
public sealed class PowerService : IDisposable
{
    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE flags);

    private bool _isAwake;

    /// <summary>Begin keeping the system awake. Idempotent.</summary>
    public void KeepAwake(bool keepDisplayOn = true)
    {
        if (_isAwake) return;

        var flags = EXECUTION_STATE.ES_CONTINUOUS
                  | EXECUTION_STATE.ES_SYSTEM_REQUIRED
                  | EXECUTION_STATE.ES_AWAYMODE_REQUIRED;
        if (keepDisplayOn) flags |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;

        var prev = SetThreadExecutionState(flags);
        if (prev == 0)
        {
            Log.Warning("SetThreadExecutionState failed; sleep prevention not active.");
            return;
        }
        _isAwake = true;
        Log.Debug("Keep-awake engaged (display={Display}).", keepDisplayOn);
    }

    /// <summary>Restore default power behavior.</summary>
    public void Release()
    {
        if (!_isAwake) return;
        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        _isAwake = false;
        Log.Debug("Keep-awake released.");
    }

    public void Dispose() => Release();
}
