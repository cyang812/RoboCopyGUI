using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoboCopyGUI.Services;

namespace RoboCopyGUI.Tests;

/// <summary>
/// In-memory <see cref="IFileSystem"/> for tests. Behaves like a tiny case-insensitive
/// Windows-style file system — enough to exercise <see cref="CopyEngine"/> end-to-end
/// without touching real disk.
/// </summary>
/// <remarks>
/// Design notes:
/// <list type="bullet">
///   <item><description>One private <c>_gate</c> lock guards every mutating + observing
///     operation so multi-step ops (find-non-clashing name, recursive delete,
///     enumerate-then-classify) are atomic — even under the engine's parallel
///     small-file workers.</description></item>
///   <item><description><c>OpenWrite</c> reserves the destination immediately
///     (matches <c>FileStream(FileMode.Create)</c> creating an empty 0-byte file
///     on open) and emulates <c>FileShare.None</c> by rejecting a second concurrent
///     writer for the same path.</description></item>
///   <item><description>Directories are implicit (any prefix of an existing file
///     path counts as an existing directory) and may also be explicit (via
///     <c>CreateDirectory</c>), matching real FS observability.</description></item>
/// </list>
/// </remarks>
internal sealed class InMemoryFileSystem : IFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explicitDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _mtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _openForWrite = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test helper: seed a file with the given bytes; auto-creates parent dirs.</summary>
    public void SeedFile(string path, byte[] contents, DateTime? mtimeUtc = null)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            EnsureParentDirsLocked(norm);
            _files[norm] = (byte[])contents.Clone();
            _mtimes[norm] = mtimeUtc ?? DateTime.UtcNow;
        }
    }

    /// <summary>Test helper: seed a file of <paramref name="size"/> bytes filled with <paramref name="fill"/>.</summary>
    public void SeedFile(string path, int size, byte fill = 0xAB, DateTime? mtimeUtc = null)
    {
        var buf = new byte[size];
        Array.Fill(buf, fill);
        SeedFile(path, buf, mtimeUtc);
    }

    /// <summary>Test helper: explicitly create an empty directory.</summary>
    public void SeedDirectory(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            EnsureParentDirsLocked(norm);
            _explicitDirs.Add(norm);
        }
    }

    /// <summary>Test helper: read back bytes written to a destination path.</summary>
    public byte[]? ReadAllBytes(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            return _files.TryGetValue(norm, out var bytes) ? (byte[])bytes.Clone() : null;
        }
    }

    // ---------- IFileSystem ----------

    public bool FileExists(string path)
    {
        string norm = Normalize(path);
        lock (_gate) return _files.ContainsKey(norm);
    }

    public bool DirectoryExists(string path)
    {
        string norm = Normalize(path);
        lock (_gate) return DirectoryExistsLocked(norm);
    }

    public void CreateDirectory(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            EnsureParentDirsLocked(norm);
            _explicitDirs.Add(norm);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            if (!DirectoryExistsLocked(norm))
                throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");

            string prefix = norm + Path.DirectorySeparatorChar;
            var childFiles = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            var childDirs = _explicitDirs.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!recursive && (childFiles.Count > 0 || childDirs.Count > 0))
                throw new IOException($"The directory is not empty: '{path}'.");

            foreach (var f in childFiles) { _files.Remove(f); _mtimes.Remove(f); }
            foreach (var d in childDirs) _explicitDirs.Remove(d);
            _explicitDirs.Remove(norm);
        }
    }

    public void DeleteFile(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            // Mirrors File.Delete: deleting a missing file is a no-op.
            _files.Remove(norm);
            _mtimes.Remove(norm);
        }
    }

    public IEnumerable<string> EnumerateFiles(string path, bool recurse)
    {
        string norm = Normalize(path);
        string prefix = norm + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            // Snapshot under the lock so callers can mutate the FS while iterating.
            return _files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(k => recurse || !k.AsSpan(prefix.Length).Contains(Path.DirectorySeparatorChar))
                .ToList();
        }
    }

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        string norm = Normalize(path);
        string prefix = norm + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _files.Keys.Concat(_explicitDirs))
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int sep = key.IndexOf(Path.DirectorySeparatorChar, prefix.Length);
                string child = sep < 0 ? key : key.Substring(0, sep);
                if (child.Length > prefix.Length) direct.Add(child);
            }
            // A pure directory (no children) that was explicitly created is also direct.
            foreach (var d in _explicitDirs)
            {
                if (d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !d.AsSpan(prefix.Length).Contains(Path.DirectorySeparatorChar))
                {
                    direct.Add(d);
                }
            }
            return direct.ToList();
        }
    }

    public long GetFileSize(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            if (!_files.TryGetValue(norm, out var bytes))
                throw new FileNotFoundException("Could not find file.", path);
            return bytes.LongLength;
        }
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            return _mtimes.TryGetValue(norm, out var t) ? t : new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }

    public void SetLastWriteTimeUtc(string path, DateTime utc)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            if (!_files.ContainsKey(norm))
                throw new FileNotFoundException("Could not find file.", path);
            _mtimes[norm] = utc;
        }
    }

    public Stream OpenRead(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            if (!_files.TryGetValue(norm, out var bytes))
                throw new FileNotFoundException("Could not find file.", path);
            // Return a snapshot so the caller's reads aren't affected by later writes.
            return new MemoryStream(bytes, writable: false);
        }
    }

    public Stream OpenWrite(string path)
    {
        string norm = Normalize(path);
        lock (_gate)
        {
            if (_openForWrite.Contains(norm))
                throw new IOException($"The file '{path}' is in use by another process.");

            // Parent directory must exist — matches FileStream(FileMode.Create) throwing
            // DirectoryNotFoundException when the parent path is missing.
            string? parent = Path.GetDirectoryName(norm);
            if (!string.IsNullOrEmpty(parent) && !DirectoryExistsLocked(parent))
                throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");

            // Reserve the file immediately so FileExists(destFile) is true even before
            // the stream is disposed (matches FileStream(FileMode.Create) semantics).
            _files[norm] = Array.Empty<byte>();
            _mtimes[norm] = DateTime.UtcNow;
            _openForWrite.Add(norm);

            return new CommitOnDisposeStream(this, norm);
        }
    }

    private void CommitWrite(string normPath, byte[] bytes)
    {
        lock (_gate)
        {
            _files[normPath] = bytes;
            _mtimes[normPath] = DateTime.UtcNow;
            _openForWrite.Remove(normPath);
        }
    }

    // ---------- helpers (must be called with _gate held when noted) ----------

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private bool DirectoryExistsLocked(string norm)
    {
        if (_explicitDirs.Contains(norm)) return true;
        string prefix = norm + Path.DirectorySeparatorChar;
        foreach (var k in _files.Keys)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var k in _explicitDirs)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void EnsureParentDirsLocked(string normPath)
    {
        // Walk parents and add them as explicit directories so DirectoryExists is
        // consistent regardless of whether files have been added yet.
        string? parent = Path.GetDirectoryName(normPath);
        while (!string.IsNullOrEmpty(parent) && parent.Length > 3 /* skip "C:\" root */)
        {
            _explicitDirs.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }

    /// <summary>
    /// A <see cref="MemoryStream"/> that commits its buffer back to the parent
    /// <see cref="InMemoryFileSystem"/> on dispose. <c>await using</c> in
    /// <see cref="CopyEngine"/> routes through <see cref="DisposeAsync"/>, which
    /// is overridden here so the commit fires either way.
    /// </summary>
    private sealed class CommitOnDisposeStream : MemoryStream
    {
        private readonly InMemoryFileSystem _fs;
        private readonly string _normPath;
        private bool _committed;

        public CommitOnDisposeStream(InMemoryFileSystem fs, string normPath)
        {
            _fs = fs;
            _normPath = normPath;
        }

        private void CommitOnce()
        {
            if (_committed) return;
            _committed = true;
            // ToArray copies exactly Length bytes (not Capacity), so SetLength-based
            // preallocation by the engine doesn't bloat the stored buffer.
            _fs.CommitWrite(_normPath, ToArray());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CommitOnce();
            base.Dispose(disposing);
        }

        public override System.Threading.Tasks.ValueTask DisposeAsync()
        {
            CommitOnce();
            return base.DisposeAsync();
        }
    }
}
