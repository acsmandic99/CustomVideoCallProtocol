using System.Net;
using VideoCall.Media.Transport;

namespace VideoCall.Media.Testing;

/// <summary>
/// Test-only transport that delays every outgoing datagram by a fixed amount.
/// A symmetric pair (same delay on both endpoints) approximates a recovery
/// round trip of 2x the delay, without any external traffic manipulator.
/// </summary>
public sealed class DelayTransportDecorator : IUdpMediaTransport
{
    private readonly IUdpMediaTransport _inner;
    private int _delayMs;

    public event DatagramReceivedHandler? DatagramReceived
    {
        add => _inner.DatagramReceived += value;
        remove => _inner.DatagramReceived -= value;
    }

    /// <summary>One-way delay applied to every outgoing datagram; adjustable while active.</summary>
    public int DelayMs
    {
        get => _delayMs;
        set => _delayMs = Math.Max(0, value);
    }

    public DelayTransportDecorator(IUdpMediaTransport inner, int delayMs)
    {
        _inner = inner;
        _delayMs = Math.Max(0, delayMs);
    }

    public void Bind(ushort localPort)
    {
        _inner.Bind(localPort);
    }

    public Task SendToAsync(ReadOnlyMemory<byte> data, IPEndPoint remote)
    {
        // copy: the caller's buffer is reused once this call returns
        byte[] copy = data.ToArray();
        _ = SendDelayedAsync(copy, remote);
        return Task.CompletedTask;
    }

    private async Task SendDelayedAsync(byte[] data, IPEndPoint remote)
    {
        try
        {
            await Task.Delay(_delayMs);
            await _inner.SendToAsync(data, remote);
        }
        catch (Exception)
        {
            // fire-and-forget: sessions routinely tear down while datagrams are still in flight
        }
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
