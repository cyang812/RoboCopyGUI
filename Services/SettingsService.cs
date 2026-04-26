using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> from a JSON file located next to the
/// running executable (NOT in %LocalAppData%) so the tool is fully portable.
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Absolute path to settings.json beside the .exe.</summary>
    public static string SettingsPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                Log.Information("No settings file found at {Path}; using defaults.", SettingsPath);
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded is null)
            {
                Log.Warning("Settings file at {Path} deserialized to null; using defaults.", SettingsPath);
                return new AppSettings();
            }

            Log.Debug("Loaded settings from {Path}.", SettingsPath);
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings from {Path}; using defaults.", SettingsPath);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(SettingsPath, json);
            Log.Debug("Saved settings to {Path}.", SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings to {Path}.", SettingsPath);
        }
    }
}
