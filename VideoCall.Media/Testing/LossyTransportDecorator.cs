using System.Net;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Framing;

namespace VideoCall.Media.Testing;

public sealed class LossyTransportDecorator : IUdpMediaTransport
{
    private readonly IUdpMediaTransport _inner;
    private readonly Random _random;
    private readonly object _randomLock = new();
    private int _dropPercent;
    private readonly Func<Packet, bool>? _dropPredicate;

    public event DatagramReceivedHandler? DatagramReceived
    {
        add => _inner.DatagramReceived += value;
        remove => _inner.DatagramReceived -= value;
    }

    public int DropPercent
    {
        get => _dropPercent;
        set => _dropPercent = Math.Clamp(value, 0, 100);
    }

    public int DroppedCount { get; private set; }

    public LossyTransportDecorator(IUdpMediaTransport inner, int dropPercent = 0, int seed = 42, Func<Packet, bool>? dropPredicate = null)
    {
        _inner = inner;
        _dropPercent = dropPercent;
        _random = new Random(seed);
        _dropPredicate = dropPredicate;
    }

    public void Bind(ushort localPort)
    {
        _inner.Bind(localPort);
    }

    public async Task SendToAsync(ReadOnlyMemory<byte> data, IPEndPoint remote)
    {
        bool drop;

        lock (_randomLock)
        {
            drop = _random.Next(100) < _dropPercent;
        }

        if (!drop && _dropPredicate is not null && PacketReader.TryParse(data.Span, out Packet? packet) && packet is not null)
        {
            drop = _dropPredicate(packet);
        }

        if (drop)
        {
            DroppedCount++;
            return;
        }

        await _inner.SendToAsync(data, remote);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
