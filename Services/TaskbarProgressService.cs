using System;
using System.Runtime.InteropServices;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>
/// Lifecycle states accepted by <see cref="TaskbarProgressService.SetState"/>.
/// Maps 1:1 to <c>TBPFLAG</c> on the native side; the names are friendlier.
/// </summary>
public enum TaskbarState
{
    /// <summary>Hide the progress bar. Default at startup and after a run.</summary>
    None,
    /// <summary>Solid green progress fill for an active copy.</summary>
    Normal,
    /// <summary>Marquee — used while preflighting (total unknown).</summary>
    Indeterminate,
    /// <summary>Yellow fill while the user has paused the run.</summary>
    Paused,
    /// <summary>Red fill — copy finished with at least one failure.</summary>
    Error,
}

/// <summary>
/// Thin wrapper over the Windows shell's <c>ITaskbarList3</c> COM interface
/// so the taskbar icon shows copy progress (and the right color for
/// running / paused / failed states). Construction is best-effort: if COM
/// activation fails (Server Core, sandboxed test host, etc.) the service
/// silently no-ops, so callers don't need to guard every call site.
/// </summary>
public sealed class TaskbarProgressService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly ITaskbarList3? _taskbar;
    private bool _disposed;

    /// <summary>
    /// Construct against the window whose taskbar icon we want to drive.
    /// <paramref name="hwnd"/> is obtained from
    /// <c>WinRT.Interop.WindowNative.GetWindowHandle(this)</c> inside a WinUI window.
    /// </summary>
    public TaskbarProgressService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (Exception ex)
        {
            // If the shell COM object can't be created, every method on this
            // service becomes a no-op. We log once and move on — never throw.
            try { Log.Debug(ex, "TaskbarList3 unavailable; taskbar progress disabled."); }
            catch { }
            _taskbar = null;
        }
    }

    /// <summary>Whether the underlying ITaskbarList3 instance is live.</summary>
    public bool IsAvailable => _taskbar is not null;

    /// <summary>
    /// Set the lifecycle state of the taskbar progress bar
    /// (Normal / Paused / Error / Indeterminate / None).
    /// </summary>
    public void SetState(TaskbarState state)
    {
        if (_taskbar is null || _hwnd == IntPtr.Zero) return;
        try
        {
            _taskbar.SetProgressState(_hwnd, ToFlag(state));
        }
        catch (Exception ex)
        {
            try { Log.Debug(ex, "ITaskbarList3.SetProgressState({State}) failed.", state); }
            catch { }
        }
    }

    /// <summary>
    /// Set the progress value as a fraction in [0, 1]. Values outside the
    /// range are clamped. No-op when the service is unavailable.
    /// </summary>
    public void SetProgress(double fraction01)
    {
        if (_taskbar is null || _hwnd == IntPtr.Zero) return;
        // Scale to a fixed denominator so we don't accidentally pass tiny
        // ulong values to the native side (10000 = 0.01% resolution, plenty
        // for the 8 px-tall taskbar bar).
        const ulong scale = 10000UL;
        double clamped = fraction01 < 0 ? 0 : fraction01 > 1 ? 1 : fraction01;
        ulong v = (ulong)(clamped * scale);
        try
        {
            _taskbar.SetProgressValue(_hwnd, v, scale);
        }
        catch (Exception ex)
        {
            try { Log.Debug(ex, "ITaskbarList3.SetProgressValue({V}/{Scale}) failed.", v, scale); }
            catch { }
        }
    }

    /// <summary>Equivalent to <c>SetState(TaskbarState.None)</c>; clears the bar.</summary>
    public void Clear() => SetState(TaskbarState.None);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Clear(); } catch { }
        if (_taskbar is not null)
        {
            try { Marshal.FinalReleaseComObject(_taskbar); } catch { }
        }
    }

    private static TBPFLAG ToFlag(TaskbarState s) => s switch
    {
        TaskbarState.None          => TBPFLAG.NOPROGRESS,
        TaskbarState.Indeterminate => TBPFLAG.INDETERMINATE,
        TaskbarState.Normal        => TBPFLAG.NORMAL,
        TaskbarState.Error         => TBPFLAG.ERROR,
        TaskbarState.Paused        => TBPFLAG.PAUSED,
        _ => TBPFLAG.NOPROGRESS,
    };

    // ============================================================
    // COM interop
    // ------------------------------------------------------------
    // ITaskbarList3 is documented at:
    //   https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-itaskbarlist3
    // It derives from ITaskbarList2 which derives from ITaskbarList, so
    // the C# interface declaration MUST list every parent method in vtable
    // order before adding our new ones — otherwise SetProgressValue ends up
    // pointing at AddTab's slot and we'd get random parameter corruption.
    // We declare unused methods with placeholder bodies; only the two
    // progress methods need real signatures.
    // ============================================================

    [Flags]
    private enum TBPFLAG : uint
    {
        NOPROGRESS    = 0,
        INDETERMINATE = 0x1,
        NORMAL        = 0x2,
        ERROR         = 0x4,
        PAUSED        = 0x8,
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3 — only the two we actually use have meaningful signatures.
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TBPFLAG tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        // ThumbBar* / SetOverlayIcon / SetThumbnailTooltip / SetThumbnailClip
        // are omitted: we don't call them, but if we ever extend this class
        // they MUST be declared in vtable order from this point onward.
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }
}
