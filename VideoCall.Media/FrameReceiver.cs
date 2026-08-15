using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;

namespace VideoCall.Media;

public sealed class FrameReceiver
{
    private static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PendingExpiry = TimeSpan.FromMilliseconds(500);

    private sealed class PendingFrame
    {
        public FrameType FrameType { get; }
        public int FragmentCount { get; }
        public byte[][] Fragments { get; }
        public bool[] Received { get; }
        public int ReceivedCount { get; set; }
        public long FirstSeenTicks { get; } = Stopwatch.GetTimestamp();

        public PendingFrame(FrameType frameType, int fragmentCount)
        {
            FrameType = frameType;
            FragmentCount = fragmentCount;
            Fragments = new byte[fragmentCount][];
            Received = new bool[fragmentCount];
        }
    }

    private readonly IUdpMediaTransport _transport;
    private readonly IPEndPoint _remote;
    private readonly IFrameSink _sink;

    private readonly Dictionary<uint, PendingFrame> _pending = new();
    private uint _highestStartedSequence;
    private bool _seenAnyFrame;
    private bool _chainBroken;
    private long _lastRequestTicks;
    private int _keyframeRequestCount;

    public int KeyframeRequestCount => _keyframeRequestCount;

    public FrameReceiver(IUdpMediaTransport transport, IPEndPoint remote, IFrameSink sink)
    {
        _transport = transport;
        _remote = remote;
        _sink = sink;
    }

    public void HandleMediaFrame(Packet packet)
    {
        if (packet.Payload.Length < 4)
        {
            return;
        }

        ushort fragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.AsSpan(0, 2));
        ushort fragmentCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.AsSpan(2, 2));

        if (fragmentIndex >= fragmentCount)
        {
            return;
        }

        uint sequence = packet.Sequence;

        if (_seenAnyFrame && sequence > _highestStartedSequence + 1)
        {
            _chainBroken = true;
        }

        if (!_seenAnyFrame || sequence > _highestStartedSequence)
        {
            _highestStartedSequence = sequence;
            _seenAnyFrame = true;
        }

        if (!_pending.TryGetValue(sequence, out PendingFrame? pending))
        {
            pending = new PendingFrame(packet.FrameType, fragmentCount);
            _pending[sequence] = pending;
        }

        if (pending.Received[fragmentIndex])
        {
            return;
        }

        var fragment = new byte[packet.Payload.Length - 4];
        packet.Payload.AsSpan(4).CopyTo(fragment);
        pending.Fragments[fragmentIndex] = fragment;
        pending.Received[fragmentIndex] = true;
        pending.ReceivedCount++;

        if (pending.ReceivedCount == pending.FragmentCount)
        {
            CompleteFrame(sequence, pending);
        }
    }

    private void CompleteFrame(uint sequence, PendingFrame pending)
    {
        ExpirePending();
        DiscardOutdated(sequence);

        if (_chainBroken && pending.FrameType == FrameType.Delta)
        {
            SendKeyframeRequest();
        }

        if (pending.FrameType == FrameType.Keyframe)
        {
            _chainBroken = false;
        }

        int totalLength = pending.Fragments.Sum(f => f.Length);
        var data = new byte[totalLength];
        int offset = 0;
        foreach (byte[] fragment in pending.Fragments)
        {
            Buffer.BlockCopy(fragment, 0, data, offset, fragment.Length);
            offset += fragment.Length;
        }

        _pending.Remove(sequence);
        _sink.OnFrameReceived(data, pending.FrameType, sequence);
    }

    private void DiscardOutdated(uint completedSequence)
    {
        List<uint>? outdated = null;

        foreach (KeyValuePair<uint, PendingFrame> pair in _pending)
        {
            if (pair.Key < completedSequence && pair.Value.ReceivedCount < pair.Value.FragmentCount)
            {
                (outdated ??= new List<uint>()).Add(pair.Key);
            }
        }

        if (outdated is not null)
        {
            foreach (uint sequence in outdated)
            {
                _pending.Remove(sequence);
            }
            _chainBroken = true;
        }
    }

    private void ExpirePending()
    {
        long now = Stopwatch.GetTimestamp();
        List<uint>? expired = null;

        foreach (KeyValuePair<uint, PendingFrame> pair in _pending)
        {
            if (Stopwatch.GetElapsedTime(pair.Value.FirstSeenTicks) > PendingExpiry)
            {
                (expired ??= new List<uint>()).Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (uint sequence in expired)
            {
                _pending.Remove(sequence);
            }
            _chainBroken = true;
        }
    }

    private void SendKeyframeRequest()
    {
        long now = Stopwatch.GetTimestamp();

        if (Stopwatch.GetElapsedTime(_lastRequestTicks) < KeyframeRequestInterval)
        {
            return;
        }

        _lastRequestTicks = now;
        _keyframeRequestCount++;

        var packet = new Packet(MessageType.KeyframeRequest, Array.Empty<byte>());
        byte[] bytes = PacketWriter.Serialize(packet);

        _ = _transport.SendToAsync(bytes, _remote);
    }
}
