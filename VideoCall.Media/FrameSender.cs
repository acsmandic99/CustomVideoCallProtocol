using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;

namespace VideoCall.Media;

public sealed class FrameSender : IDisposable
{
    private sealed record OutgoingFrame(byte[] Data, FrameType FrameType);

    private readonly IUdpMediaTransport _transport;
    private readonly IPEndPoint _remote;
    private readonly Channel<OutgoingFrame> _queue;
    private readonly Task _worker;
    private uint _nextSequence;

    public event Action? KeyframeRequested;

    public FrameSender(IUdpMediaTransport transport, IPEndPoint remote)
    {
        _transport = transport;
        _remote = remote;
        _queue = Channel.CreateUnbounded<OutgoingFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(SendLoopAsync);
    }

    public void SendFrame(byte[] data, FrameType frameType)
    {
        _queue.Writer.TryWrite(new OutgoingFrame(data, frameType));
    }

    public void HandleKeyframeRequest()
    {
        KeyframeRequested?.Invoke();
    }

    private async Task SendLoopAsync()
    {
        await foreach (OutgoingFrame frame in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await SendFragmentsAsync(frame.Data, frame.FrameType);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private async Task SendFragmentsAsync(byte[] data, FrameType frameType)
    {
        uint sequence = ++_nextSequence;
        int fragmentCount = (data.Length + MediaConstants.FragmentSize - 1) / MediaConstants.FragmentSize;

        for (ushort fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
        {
            int offset = fragmentIndex * MediaConstants.FragmentSize;
            int length = Math.Min(MediaConstants.FragmentSize, data.Length - offset);

            var payload = new byte[4 + length];
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), fragmentIndex);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)fragmentCount);
            Buffer.BlockCopy(data, offset, payload, 4, length);

            var packet = new Packet(MessageType.MediaFrame, payload, sequence, frameType);
            byte[] bytes = PacketWriter.Serialize(packet);

            await _transport.SendToAsync(bytes, _remote);
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
    }
}
