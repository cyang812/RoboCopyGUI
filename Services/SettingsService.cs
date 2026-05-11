using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> from a JSON file located next to the
/// running executable (NOT in %LocalAppData%) so the tool is fully portable.
/// </summary>
public static class SettingsService
{
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
            AppSettings? loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
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
            string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(SettingsPath, json);
            Log.Debug("Saved settings to {Path}.", SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings to {Path}.", SettingsPath);
        }
    }
}

/// <summary>
/// Compile-time-generated, trim-safe (and faster) serializer metadata for
/// <see cref="AppSettings"/>. Replaces the reflection-based JsonSerializer overloads
/// that emitted IL2026 trim-analysis warnings under PublishTrimmed.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}
