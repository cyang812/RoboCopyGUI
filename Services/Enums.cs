namespace RoboCopyGUI.Services;

/// <summary>What to do when a file already exists at the destination.</summary>
public enum ConflictPolicy
{
    /// <summary>Always overwrite the existing file (current default).</summary>
    Overwrite,

    /// <summary>Leave the existing file alone; mark the source as Skipped.</summary>
    Skip,

    /// <summary>
    /// Skip if the destination has the same size AND same last-write time (within 2s);
    /// otherwise overwrite. Useful for resuming partial transfers.
    /// </summary>
    SkipIfSame,

    /// <summary>
    /// Append " (1)", " (2)", &#x2026; before the extension to find a non-conflicting name.
    /// </summary>
    Rename,
}

/// <summary>Lifecycle status of a single source item shown in the UI.</summary>
public enum ItemStatus
{
    Queued,
    InProgress,
    Done,
    Failed,
    Skipped,
}
