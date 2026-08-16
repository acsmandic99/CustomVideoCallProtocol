using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;

namespace VideoCall.Media;

public sealed class FrameSender : IDisposable
{
    private const int RetransmitBufferSize = 256;

    private sealed record OutgoingFrame(byte[] Data, FrameType FrameType, VideoCodec VideoCodec);

    private readonly IUdpMediaTransport _transport;
    private readonly IPEndPoint _remote;
    private readonly Channel<OutgoingFrame> _queue;
    private readonly Task _worker;
    private readonly object _recentLock = new();
    private readonly Dictionary<uint, (byte[] Data, FrameType FrameType, VideoCodec Codec)> _recent = new();
    private readonly Queue<uint> _recentOrder = new();
    private uint _nextSequence;

    public event Action? KeyframeRequested;

    public int RetransmittedFrames { get; private set; }

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

    public void SendFrame(byte[] data, FrameType frameType, VideoCodec videoCodec)
    {
        _queue.Writer.TryWrite(new OutgoingFrame(data, frameType, videoCodec));
    }

    public void HandleKeyframeRequest(uint[] missingSequences)
    {
        bool needKeyframe = false;

        foreach (uint missingSequence in missingSequences)
        {
            (byte[] Data, FrameType FrameType, VideoCodec Codec) frame;

            lock (_recentLock)
            {
                if (missingSequence == 0 || !_recent.TryGetValue(missingSequence, out frame) || frame.FrameType != FrameType.Delta)
                {
                    needKeyframe = true;
                    continue;
                }
            }

            RetransmittedFrames++;
            _ = RetransmitAsync(frame, missingSequence);
        }

        if (needKeyframe || missingSequences.Length == 0)
        {
            KeyframeRequested?.Invoke();
        }
    }

    private async Task RetransmitAsync((byte[] Data, FrameType FrameType, VideoCodec Codec) frame, uint sequence)
    {
        try
        {
            await SendFragmentsAsync(frame.Data, frame.FrameType, frame.Codec, sequence);
        }
        catch (Exception)
        {
        }
    }

    private async Task SendLoopAsync()
    {
        await foreach (OutgoingFrame frame in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await SendFragmentsAsync(frame.Data, frame.FrameType, frame.VideoCodec, null);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private async Task SendFragmentsAsync(byte[] data, FrameType frameType, VideoCodec videoCodec, uint? retransmitSequence)
    {
        uint sequence = retransmitSequence ?? ++_nextSequence;

        if (retransmitSequence is null)
        {
            lock (_recentLock)
            {
                _recent[sequence] = (data, frameType, videoCodec);
                _recentOrder.Enqueue(sequence);

                while (_recentOrder.Count > RetransmitBufferSize)
                {
                    _recent.Remove(_recentOrder.Dequeue(), out _);
                }
            }
        }

        int fragmentCount = (data.Length + MediaConstants.FragmentSize - 1) / MediaConstants.FragmentSize;

        for (ushort fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
        {
            int offset = fragmentIndex * MediaConstants.FragmentSize;
            int length = Math.Min(MediaConstants.FragmentSize, data.Length - offset);

            var payload = new byte[5 + length];
            payload[0] = (byte)videoCodec;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1, 2), fragmentIndex);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3, 2), (ushort)fragmentCount);
            Buffer.BlockCopy(data, offset, payload, 5, length);

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
