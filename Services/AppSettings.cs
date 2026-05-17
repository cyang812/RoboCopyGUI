namespace RoboCopyGUI.Services;

/// <summary>
/// User-facing persistent settings. Stored as JSON in the same folder as the executable.
/// Add new properties here with sensible defaults; older settings.json files will simply
/// keep using the defaults until the user changes them.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Last selected destination folder. Empty string = none.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Whether the "Delete source files" (Move mode) checkbox should be on at startup.</summary>
    public bool DeleteSourceAfterCopy { get; set; }

    /// <summary>
    /// Logging verbosity. One of: Verbose, Debug, Information, Warning, Error, Fatal.
    /// Defaults to Information.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>App theme: System | Light | Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// What to do when a destination file already exists.
    /// One of: Overwrite | Skip | SkipIfSame | Rename. Defaults to Overwrite (legacy behavior).
    /// </summary>
    public string ConflictPolicy { get; set; } = "Overwrite";

    /// <summary>
    /// Prevent the PC from sleeping / display from turning off while a copy is running.
    /// Restored when the copy ends.
    /// </summary>
    public bool KeepAwakeDuringCopy { get; set; } = true;

    /// <summary>
    /// Recently used destination folders, most-recent first. Capped at <see cref="MaxDestinationHistory"/>.
    /// </summary>
    public System.Collections.Generic.List<string> DestinationHistory { get; set; } = new();

    public const int MaxDestinationHistory = 12;

    /// <summary>Show a toast notification when a copy run finishes.</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>Play a short system sound when a copy run finishes.</summary>
    public bool PlaySoundOnCompletion { get; set; } = true;

    /// <summary>Display per-second network throughput in the status line during copies.</summary>
    public bool ShowNetworkThroughput { get; set; } = true;

    /// <summary>
    /// Maximum number of "small" files copied concurrently inside a directory tree
    /// (or among the top-level queue). 1 disables parallelism. Capped to a sane limit.
    /// </summary>
    public int MaxParallelSmallFiles { get; set; } = 4;

    /// <summary>Files at or below this size (bytes) are eligible for parallel copy.
    /// Larger files run sequentially so their pipelined I/O isn't starved.</summary>
    public long SmallFileThresholdBytes { get; set; } = 10L * 1024 * 1024; // 10 MiB

    /// <summary>Last folder the Add Files / Add Folder picker opened from.</summary>
    public string LastSourceFolder { get; set; } = string.Empty;

    /// <summary>
    /// Check the GitHub Releases API once per launch and show an inline "update
    /// available" InfoBar when a newer release exists. Disable to opt out of
    /// the (one) outbound HTTP call per launch.
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;
}
