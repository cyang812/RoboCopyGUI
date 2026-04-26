using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

/// <summary>
/// Minimal in-memory <see cref="ICopyItem"/> implementation for tests.
/// Records the sequence of status transitions so assertions can target them.
/// </summary>
internal sealed class FakeCopyItem : ICopyItem
{
    public string Path { get; }
    public List<(ItemStatus Status, string? Error)> Transitions { get; } = new();
    public ItemStatus LastStatus => Transitions.Count == 0 ? ItemStatus.Queued : Transitions[^1].Status;

    public FakeCopyItem(string path) => Path = path;

    public void SetStatus(ItemStatus status, string? error = null) =>
        Transitions.Add((status, error));
}

/// <summary>
/// Disposable working directory for tests. Created under %TEMP% with a unique name
/// and recursively deleted on dispose. Each test gets its own.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; }

    public TempDir()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "RoboCopyGUI.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string MakeFile(string relative, byte[] contents)
    {
        string full = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    public string MakeFile(string relative, int sizeBytes, byte fill = 0xAB)
    {
        var buf = new byte[sizeBytes];
        Array.Fill(buf, fill);
        return MakeFile(relative, buf);
    }

    public string MakeDir(string relative)
    {
        string full = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort */ }
    }
}
