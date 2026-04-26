using System;
using Microsoft.UI.Xaml;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>
/// Applies a user-chosen theme (System | Light | Dark) to the app's root content.
/// Persisted by <see cref="AppSettings.Theme"/>.
/// </summary>
public static class ThemeService
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>Apply the saved <see cref="AppSettings.Theme"/> to the given window's root element.</summary>
    public static void Apply(Window window, string theme)
    {
        if (window?.Content is not FrameworkElement root)
        {
            Log.Debug("ThemeService.Apply: window has no FrameworkElement content yet.");
            return;
        }

        ElementTheme target = theme switch
        {
            Light => ElementTheme.Light,
            Dark => ElementTheme.Dark,
            _ => ElementTheme.Default, // System
        };

        if (root.RequestedTheme == target) return;
        root.RequestedTheme = target;
        Log.Information("Applied theme: {Theme}", theme);
    }

    /// <summary>Return the next theme in the rotation: System -&gt; Light -&gt; Dark -&gt; System.</summary>
    public static string Cycle(string current) => current switch
    {
        System => Light,
        Light => Dark,
        _ => System,
    };
}
