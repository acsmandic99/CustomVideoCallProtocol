using System.Net;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;

namespace VideoCall.Media;

public sealed class MediaSession : IDisposable
{
    private readonly IUdpMediaTransport _transport;
    private readonly FrameSender _sender;
    private readonly FrameReceiver _receiver;

    public event Action? KeyframeRequested
    {
        add => _sender.KeyframeRequested += value;
        remove => _sender.KeyframeRequested -= value;
    }

    public int KeyframeRequestCount => _receiver.KeyframeRequestCount;
    public int NackCount => _receiver.NackCount;

    public MediaSession(IUdpMediaTransport transport, IPEndPoint remote, IFrameSink sink)
    {
        _transport = transport;
        _sender = new FrameSender(transport, remote);
        _receiver = new FrameReceiver(transport, remote, sink);
    }

    public void Start(ushort localPort)
    {
        _transport.DatagramReceived += OnDatagramReceived;
        _transport.Bind(localPort);
    }

    public void SendFrame(byte[] data, FrameType frameType, VideoCodec videoCodec)
    {
        _sender.SendFrame(data, frameType, videoCodec);
    }

    public event Action<Exception>? SendError
    {
        add => _sender.SendError += value;
        remove => _sender.SendError -= value;
    }

    public int ReceivedDatagrams { get; private set; }

    private void OnDatagramReceived(ReadOnlyMemory<byte> data, IPEndPoint from)
    {
        ReceivedDatagrams++;

        if (!PacketReader.TryParse(data.Span, out Packet? packet) || packet is null)
        {
            return;
        }

        switch (packet.MessageType)
        {
            case MessageType.MediaFrame:
                _receiver.HandleMediaFrame(packet);
                break;
            case MessageType.KeyframeRequest:
                if (packet.Payload.Length >= 1)
                {
                    int count = packet.Payload[0];
                    var sequences = new uint[count];

                    for (int i = 0; i < count && 1 + (i + 1) * 4 <= packet.Payload.Length; i++)
                    {
                        sequences[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.Payload.AsSpan(1 + i * 4, 4));
                    }

                    _sender.HandleKeyframeRequest(sequences);
                }
                break;
        }
    }

    public void Dispose()
    {
        _transport.DatagramReceived -= OnDatagramReceived;
        _sender.Dispose();
        _transport.Dispose();
    }
}
