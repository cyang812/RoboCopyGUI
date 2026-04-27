using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Serilog;
using System;
using System.Runtime.InteropServices;

namespace RoboCopyGUI.Services;

/// <summary>
/// Cross-cutting completion notifications: optional toast + optional system sound.
/// Toasts use the unpackaged Windows App SDK App Notifications API (works without
/// an MSIX identity from Windows 10 1809+). If toast registration fails (e.g. on a
/// stripped-down OS image), the sound still plays and we just log the failure.
/// </summary>
public static class NotificationService
{
    private static bool s_registered;

    /// <summary>Call once at startup. Safe to call repeatedly.</summary>
    public static void Initialize()
    {
        if (s_registered) return;
        try
        {
            AppNotificationManager.Default.Register();
            s_registered = true;
            Log.Debug("AppNotificationManager registered.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AppNotificationManager.Register failed; toasts disabled.");
        }
    }

    /// <summary>Call on app exit. Safe even if Initialize never succeeded.</summary>
    public static void Shutdown()
    {
        if (!s_registered) return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "AppNotificationManager.Unregister failed.");
        }
        s_registered = false;
    }

    /// <summary>Fire a completion toast and/or system sound according to user settings.</summary>
    public static void NotifyCompletion(string title, string body, bool failed)
    {
        var s = App.Settings;

        if (s.PlaySoundOnCompletion)
        {
            try
            {
                // MB_ICONASTERISK (0x40) for success, MB_ICONHAND (0x10) for failure.
                MessageBeep(failed ? 0x10u : 0x40u);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to play completion sound.");
            }
        }

        if (s.NotifyOnCompletion && s_registered)
        {
            try
            {
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(body)
                    .BuildNotification();
                AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to show completion toast.");
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);
}
