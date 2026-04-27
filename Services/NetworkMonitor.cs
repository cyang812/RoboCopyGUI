using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;

namespace RoboCopyGUI.Services;

/// <summary>
/// Lightweight network adapter throughput sampler. Polls
/// <see cref="NetworkInterface.GetAllNetworkInterfaces"/> and computes per-second
/// send/receive rates for the busiest non-loopback adapter.
/// </summary>
/// <remarks>
/// Used purely for UI display. Read-only, no impact on the copy itself.
/// Snapshots are taken on a short timer, deltas divided by the wall-clock
/// interval since the previous sample.
/// </remarks>
public sealed class NetworkMonitor : IDisposable
{
    private readonly Timer _timer;
    private readonly object _lock = new();

    private long _lastBytesSent;
    private long _lastBytesReceived;
    private DateTime _lastSampleUtc;
    private string _activeAdapterName = string.Empty;

    public double SendBytesPerSecond { get; private set; }
    public double ReceiveBytesPerSecond { get; private set; }
    public string AdapterName => _activeAdapterName;

    public event Action? Sampled;

    public NetworkMonitor(TimeSpan? interval = null)
    {
        var period = interval ?? TimeSpan.FromMilliseconds(750);
        _lastSampleUtc = DateTime.UtcNow;
        Snapshot(out _lastBytesSent, out _lastBytesReceived, out _activeAdapterName);
        _timer = new Timer(_ => Tick(), null, period, period);
    }

    private void Tick()
    {
        try
        {
            Snapshot(out long sent, out long recv, out string name);
            var now = DateTime.UtcNow;
            double seconds;
            lock (_lock)
            {
                seconds = (now - _lastSampleUtc).TotalSeconds;
                if (seconds <= 0) return;
                long deltaSent = Math.Max(0, sent - _lastBytesSent);
                long deltaRecv = Math.Max(0, recv - _lastBytesReceived);
                SendBytesPerSecond = deltaSent / seconds;
                ReceiveBytesPerSecond = deltaRecv / seconds;
                _lastBytesSent = sent;
                _lastBytesReceived = recv;
                _lastSampleUtc = now;
                _activeAdapterName = name;
            }
            Sampled?.Invoke();
        }
        catch
        {
            // Sampling is opportunistic; never let timer exceptions kill the app.
        }
    }

    /// <summary>
    /// Sums Bytes/Sent across all up, non-loopback IPv4 adapters and returns the name
    /// of the single busiest adapter (highest combined send+receive total).
    /// Sampling all adapters together is more robust than trying to guess which
    /// adapter the SMB traffic is leaving on.
    /// </summary>
    private static void Snapshot(out long bytesSent, out long bytesReceived, out string adapterName)
    {
        bytesSent = 0;
        bytesReceived = 0;
        adapterName = string.Empty;
        long bestTotal = -1;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = nic.GetIPv4Statistics();
                bytesSent += stats.BytesSent;
                bytesReceived += stats.BytesReceived;
                long total = stats.BytesSent + stats.BytesReceived;
                if (total > bestTotal)
                {
                    bestTotal = total;
                    adapterName = nic.Name;
                }
            }
            catch
            {
                // Some virtual adapters refuse stats queries; ignore them.
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
