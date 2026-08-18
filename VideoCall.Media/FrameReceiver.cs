using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;

namespace VideoCall.Media;

public sealed class FrameReceiver
{
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HoleTimeout = TimeSpan.FromMilliseconds(400);
    private const int MaxHolesPerRequest = 16;

    private sealed class PendingFrame
    {
        public FrameType FrameType { get; }
        public VideoCodec VideoCodec { get; }
        public int FragmentCount { get; }
        public byte[][] Fragments { get; }
        public bool[] Received { get; }
        public int ReceivedCount { get; set; }
        public long FirstSeenTicks { get; } = Stopwatch.GetTimestamp();

        public PendingFrame(FrameType frameType, VideoCodec videoCodec, int fragmentCount)
        {
            FrameType = frameType;
            VideoCodec = videoCodec;
            FragmentCount = fragmentCount;
            Fragments = new byte[fragmentCount][];
            Received = new bool[fragmentCount];
        }
    }

    private sealed class CompletedFrame
    {
        public byte[] Data { get; }
        public FrameType FrameType { get; }
        public VideoCodec VideoCodec { get; }
        public long FirstSeenTicks { get; } = Stopwatch.GetTimestamp();

        public CompletedFrame(byte[] data, FrameType frameType, VideoCodec videoCodec)
        {
            Data = data;
            FrameType = frameType;
            VideoCodec = videoCodec;
        }
    }

    private readonly IUdpMediaTransport _transport;
    private readonly IPEndPoint _remote;
    private readonly IFrameSink _sink;

    private readonly Dictionary<uint, PendingFrame> _pending = new();
    private readonly Dictionary<uint, CompletedFrame> _hold = new();
    private uint _lastDelivered;
    private bool _deliveredAny;
    private bool _awaitingKeyframe;
    private uint _highestSeen;
    private readonly HashSet<uint> _audioSeen = new();
    private long _lastRequestTicks;
    private long _lastProgressTicks = Stopwatch.GetTimestamp();

    public int KeyframeRequestCount { get; private set; }
    public int NackCount { get; private set; }

    public FrameReceiver(IUdpMediaTransport transport, IPEndPoint remote, IFrameSink sink)
    {
        _transport = transport;
        _remote = remote;
        _sink = sink;
    }

    public void HandleMediaFrame(Packet packet)
    {
        if (packet.Payload.Length < 5)
        {
            return;
        }

        var videoCodec = (VideoCodec)packet.Payload[0];
        ushort fragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.AsSpan(1, 2));
        ushort fragmentCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.AsSpan(3, 2));

        if (fragmentIndex >= fragmentCount)
        {
            return;
        }

        uint sequence = packet.Sequence;

        if (_deliveredAny && sequence <= _lastDelivered)
        {
            return;
        }

        if (sequence > _highestSeen)
        {
            _highestSeen = sequence;
        }

        ExpireStale();

        if (!_pending.TryGetValue(sequence, out PendingFrame? pending))
        {
            pending = new PendingFrame(packet.FrameType, videoCodec, fragmentCount);
            _pending[sequence] = pending;
        }

        if (pending.Received[fragmentIndex])
        {
            return;
        }

        var fragment = new byte[packet.Payload.Length - 5];
        packet.Payload.AsSpan(5).CopyTo(fragment);
        pending.Fragments[fragmentIndex] = fragment;
        pending.Received[fragmentIndex] = true;
        pending.ReceivedCount++;

        if (pending.ReceivedCount == pending.FragmentCount)
        {
            int totalLength = pending.Fragments.Sum(f => f.Length);
            var data = new byte[totalLength];
            int offset = 0;

            foreach (byte[] part in pending.Fragments)
            {
                Buffer.BlockCopy(part, 0, data, offset, part.Length);
                offset += part.Length;
            }

            _pending.Remove(sequence);

            if (pending.FrameType == FrameType.Audio)
            {
                _audioSeen.Add(sequence);
                _sink.OnFrameReceived(data, pending.FrameType, sequence, pending.VideoCodec);
                return;
            }

            _hold[sequence] = new CompletedFrame(data, pending.FrameType, pending.VideoCodec);
        }

        DeliverInOrder();
        RequestMissing();
    }

    private void DeliverInOrder()
    {
        if (!_deliveredAny)
        {
            uint? firstSeq = null;

            foreach (uint seq in _hold.Keys)
            {
                if (firstSeq is null || seq < firstSeq)
                {
                    firstSeq = seq;
                }
            }

            if (firstSeq is null)
            {
                return;
            }

            if (_awaitingKeyframe && _hold[firstSeq.Value].FrameType != FrameType.Keyframe)
            {
                return;
            }

            Deliver(firstSeq.Value);
        }

        if (_awaitingKeyframe)
        {
            uint? keySeq = null;

            foreach (KeyValuePair<uint, CompletedFrame> pair in _hold)
            {
                if (pair.Value.FrameType == FrameType.Keyframe && (keySeq is null || pair.Key < keySeq))
                {
                    keySeq = pair.Key;
                }
            }

            if (keySeq is null)
            {
                return;
            }

            List<uint> stale = _hold.Keys.Where(s => s < keySeq.Value).ToList();
            foreach (uint seq in stale)
            {
                _hold.Remove(seq);
            }

            _awaitingKeyframe = false;
            Deliver(keySeq.Value);
        }

        while (true)
        {
            uint next = _lastDelivered + 1;

            if (_audioSeen.Contains(next))
            {
                _lastDelivered = next;
                continue;
            }

            if (!_hold.Remove(next, out CompletedFrame? frame))
            {
                break;
            }

            DeliverFrame(frame, next);
            _lastDelivered = next;
            _lastProgressTicks = Stopwatch.GetTimestamp();
        }
    }

    private void Deliver(uint sequence)
    {
        if (_hold.Remove(sequence, out CompletedFrame? frame))
        {
            _lastDelivered = sequence;
            _deliveredAny = true;
            _lastProgressTicks = Stopwatch.GetTimestamp();
            DeliverFrame(frame, sequence);
        }
    }

    private void DeliverFrame(CompletedFrame frame, uint sequence)
    {
        _sink.OnFrameReceived(frame.Data, frame.FrameType, sequence, frame.VideoCodec);
    }

    private void RequestMissing()
    {
        if (!_deliveredAny || _awaitingKeyframe)
        {
            return;
        }

        List<uint>? holes = null;

        for (uint seq = _lastDelivered + 1; seq <= _highestSeen && (holes?.Count ?? 0) < MaxHolesPerRequest; seq++)
        {
            if (!_hold.ContainsKey(seq) && !_audioSeen.Contains(seq))
            {
                (holes ??= new List<uint>()).Add(seq);
            }
        }

        if (holes is null || holes.Count == 0)
        {
            return;
        }

        SendRequest(holes, isNack: true);
    }

    private void ExpireStale()
    {
        long now = Stopwatch.GetTimestamp();
        List<uint>? expired = null;

        foreach (KeyValuePair<uint, CompletedFrame> pair in _hold)
        {
            if (Stopwatch.GetElapsedTime(pair.Value.FirstSeenTicks) > HoleTimeout)
            {
                (expired ??= new List<uint>()).Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (uint seq in expired)
            {
                _hold.Remove(seq);
            }
        }

        bool progressedRecently = Stopwatch.GetElapsedTime(_lastProgressTicks) < HoleTimeout;

        if (!progressedRecently && !_awaitingKeyframe)
        {
            _awaitingKeyframe = true;
            _pending.Clear();
            _lastProgressTicks = Stopwatch.GetTimestamp();
            SendRequest(new List<uint>(), isNack: false);
        }
    }

    private void SendRequest(List<uint> missingSequences, bool isNack)
    {
        long now = Stopwatch.GetTimestamp();

        if (Stopwatch.GetElapsedTime(_lastRequestTicks) < RequestInterval)
        {
            return;
        }

        _lastRequestTicks = now;

        if (isNack)
        {
            NackCount++;
        }
        else
        {
            KeyframeRequestCount++;
        }

        var payload = new byte[1 + missingSequences.Count * 4];
        payload[0] = (byte)missingSequences.Count;

        for (int i = 0; i < missingSequences.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1 + i * 4, 4), missingSequences[i]);
        }
        var packet = new Packet(MessageType.KeyframeRequest, payload);
        byte[] bytes = PacketWriter.Serialize(packet);

        _ = _transport.SendToAsync(bytes, _remote);
    }

    public void Dispose()
    {
    }
}
