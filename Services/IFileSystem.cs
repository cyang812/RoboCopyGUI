using System;
using System.Collections.Generic;
using System.IO;

namespace RoboCopyGUI.Services;

/// <summary>
/// Abstraction over the file-system operations used by <see cref="CopyEngine"/>.
/// Exists so the engine can be unit-tested against an in-memory fake without
/// touching real disk, and so future features (verify-after-copy, virtual queues,
/// dry-run mode) can intercept I/O without rewriting the engine.
/// </summary>
/// <remarks>
/// Contract notes for implementers:
/// <list type="bullet">
///   <item><description>All methods take fully-qualified or relative paths exactly as
///     <see cref="System.IO"/> would accept them.</description></item>
///   <item><description>Stream lifetimes must mirror <see cref="FileStream"/>: the file
///     must become observable to <see cref="FileExists"/> as soon as
///     <see cref="OpenWrite"/> returns (the destination is reserved), and writes are
///     visible to subsequent readers after the stream is disposed.</description></item>
///   <item><description><see cref="OpenWrite"/> must refuse a second concurrent
///     writer for the same path (real FS uses <c>FileShare.None</c>).</description></item>
/// </list>
/// </remarks>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);

    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);

    /// <summary>
    /// Enumerate files under <paramref name="path"/>. When <paramref name="recurse"/>
    /// is true, walks subdirectories; otherwise returns only direct children.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string path, bool recurse);

    /// <summary>Enumerate immediate subdirectories of <paramref name="path"/>.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    long GetFileSize(string path);
    DateTime GetLastWriteTimeUtc(string path);
    void SetLastWriteTimeUtc(string path, DateTime utc);

    /// <summary>Open an existing file for read. Caller disposes.</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// Create or truncate the file at <paramref name="path"/> and return a writable
    /// stream. The destination is reserved before this method returns.
    /// </summary>
    Stream OpenWrite(string path);
}

/// <summary>
/// Default <see cref="IFileSystem"/> backed by <see cref="System.IO"/>.
/// Used by production code (<see cref="CopyEngine"/> defaults to
/// <see cref="Instance"/>) and by tests that want real-disk semantics.
/// </summary>
public sealed class RealFileSystem : IFileSystem
{
    /// <summary>Process-wide singleton; the class is stateless.</summary>
    public static readonly RealFileSystem Instance = new();

    private RealFileSystem() { }

    // Tuning notes for OpenRead/OpenWrite:
    //   - 1 MiB buffer amortizes SMB packet overhead.
    //   - FileOptions.Asynchronous: enables overlapped I/O at the Win32 layer.
    //   - FileOptions.SequentialScan: hint to the OS read-cache, helps spinning HDDs.
    //   - We deliberately do NOT use FileOptions.WriteThrough — it disables the SMB
    //     server's write cache and tanks throughput on home NAS.
    private const int BufferSize = 1 * 1024 * 1024;
    private const FileOptions ReadOpts = FileOptions.Asynchronous | FileOptions.SequentialScan;
    private const FileOptions WriteOpts = FileOptions.Asynchronous;

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public void DeleteFile(string path) => File.Delete(path);

    public IEnumerable<string> EnumerateFiles(string path, bool recurse) =>
        Directory.EnumerateFiles(path, "*",
            recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateDirectories(string path) =>
        Directory.EnumerateDirectories(path);

    public long GetFileSize(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
    public void SetLastWriteTimeUtc(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, ReadOpts);

    public Stream OpenWrite(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, WriteOpts);
}
