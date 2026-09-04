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

    // an incomplete frame older than this is treated as lossy, not as in-flight;
    // at 30 fps over a LAN reassembly takes well under a millisecond
    private static readonly TimeSpan PendingStaleTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan KeyframeResyncTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan KeyframeNackRttLimit = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan AudioPendingStaleTimeout = TimeSpan.FromMilliseconds(500);
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
    private readonly Dictionary<uint, PendingFrame> _audioPending = new();
    private long _lastRequestTicks;
    private long _lastProgressTicks = Stopwatch.GetTimestamp();

    // Adaptive recovery: the receiver measures how long a NACK takes to repair a hole
    // (NACK travel + retransmission travel = one round trip) and switches to pure
    // keyframe requests once a retransmission could no longer beat the reordering window.
    private readonly Dictionary<uint, long> _repairProbeTicks = new();
    private long _lastNackTicks;
    private long _lastPliTicks;
    private long _pliSentTicks;
    private double _smoothedRepairRttMs;

    public int KeyframeRequestCount { get; private set; }
    public int NackCount { get; private set; }

    /// <summary>Smoothed NACK-to-repair round trip in milliseconds (0 = not measured yet).</summary>
    public double SmoothedRepairRttMs => _smoothedRepairRttMs;

    /// <summary>false delivers frames as soon as they reassemble, without reorder/NACK/PLI (raw UDP baseline).</summary>
    public bool RecoveryEnabled { get; set; } = true;

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

        if (packet.FrameType == FrameType.Audio)
        {
            HandleAudioPacket(packet, videoCodec, fragmentIndex, fragmentCount, sequence);
            return;
        }

        if (_deliveredAny && sequence <= _lastDelivered)
        {
            return;
        }

        if (sequence > _highestSeen)
        {
            _highestSeen = sequence;
        }

        if (RecoveryEnabled)
        {
            ExpireStale();
        }

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
            _hold[sequence] = new CompletedFrame(data, pending.FrameType, pending.VideoCodec);

            if (_repairProbeTicks.Remove(sequence, out long probeTick))
            {
                double sampleMs = Stopwatch.GetElapsedTime(probeTick).TotalMilliseconds;
                _smoothedRepairRttMs = _smoothedRepairRttMs == 0
                    ? sampleMs
                    : 0.875 * _smoothedRepairRttMs + 0.125 * sampleMs;
            }
        }

        if (!RecoveryEnabled)
        {
            if (_hold.Remove(sequence, out CompletedFrame? immediate))
            {
                _lastDelivered = sequence;
                _deliveredAny = true;
                _sink.OnFrameReceived(immediate.Data, immediate.FrameType, sequence, immediate.VideoCodec);
            }

            return;
        }

        DeliverInOrder();
        RequestMissing();
    }


    private void HandleAudioPacket(Packet packet, VideoCodec videoCodec, ushort fragmentIndex, ushort fragmentCount, uint sequence)
    {
        // audio has no NACK/PLI recovery: a datagram lost mid-frame would otherwise
        // linger in _audioPending forever and slowly leak memory on lossy links
        List<uint>? stale = null;

        foreach (KeyValuePair<uint, PendingFrame> pair in _audioPending)
        {
            if (Stopwatch.GetElapsedTime(pair.Value.FirstSeenTicks) > AudioPendingStaleTimeout)
            {
                (stale ??= new List<uint>()).Add(pair.Key);
            }
        }

        if (stale is not null)
        {
            foreach (uint seq in stale)
            {
                _audioPending.Remove(seq);
            }
        }

        if (!_audioPending.TryGetValue(sequence, out PendingFrame? pending))
        {
            pending = new PendingFrame(FrameType.Audio, videoCodec, fragmentCount);
            _audioPending[sequence] = pending;
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

        if (pending.ReceivedCount != pending.FragmentCount)
        {
            return;
        }

        int totalLength = pending.Fragments.Sum(f => f.Length);
        var data = new byte[totalLength];
        int offset = 0;

        foreach (byte[] part in pending.Fragments)
        {
            Buffer.BlockCopy(part, 0, data, offset, part.Length);
            offset += part.Length;
        }

        _audioPending.Remove(sequence);
        _sink.OnFrameReceived(data, FrameType.Audio, sequence, videoCodec);
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

            if (!_hold.Remove(next, out CompletedFrame? frame))
            {
                // blocked at a hole. Two resync cases, both requiring a complete
                // keyframe to be buffered ahead (TryResyncToKeyframe):
                // 1. the blocker is a partial keyframe older than PendingStaleTimeout —
                //    keyframe fragments are lost for good, nothing can fill this hole
                // 2. no progress for KeyframeResyncTimeout — the hole survived several
                //    NACK cycles, so waiting longer only lets the backlog expire
                bool stalePartialKeyframe = _pending.TryGetValue(next, out PendingFrame? blocked) &&
                    blocked.FrameType == FrameType.Keyframe &&
                    Stopwatch.GetElapsedTime(blocked.FirstSeenTicks) > PendingStaleTimeout;

                bool stalled = Stopwatch.GetElapsedTime(_lastProgressTicks) > KeyframeResyncTimeout;

                if ((stalePartialKeyframe || stalled) && TryResyncToKeyframe())
                {
                    continue;
                }

                break;
            }

            DeliverFrame(frame, next);
            _lastDelivered = next;
            _lastProgressTicks = Stopwatch.GetTimestamp();
        }
    }

    private bool TryResyncToKeyframe()
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
            return false;
        }

        List<uint> stale = _hold.Keys.Where(s => s < keySeq.Value).ToList();

        foreach (uint seq in stale)
        {
            _hold.Remove(seq);
        }

        Deliver(keySeq.Value);
        _awaitingKeyframe = false;
        return true;
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
        if (frame.FrameType == FrameType.Keyframe && _pliSentTicks != 0)
        {
            double sampleMs = Stopwatch.GetElapsedTime(_pliSentTicks).TotalMilliseconds;
            _smoothedRepairRttMs = _smoothedRepairRttMs == 0
                ? sampleMs
                : 0.875 * _smoothedRepairRttMs + 0.125 * sampleMs;
            _pliSentTicks = 0;
        }

        _sink.OnFrameReceived(frame.Data, frame.FrameType, sequence, frame.VideoCodec);
    }

    private void RequestMissing()
    {
        if (!_deliveredAny)
        {
            return;
        }

        List<uint>? holes = null;

        for (uint seq = _lastDelivered + 1; seq <= _highestSeen && (holes?.Count ?? 0) < MaxHolesPerRequest; seq++)
        {
            bool inHold = _hold.ContainsKey(seq);

            // a frame being assembled is in-flight, not a hole — unless it has been
            // incomplete too long, which means a fragment was lost and must be retransmitted
            bool inFlight = _pending.TryGetValue(seq, out PendingFrame? pending) &&
                            Stopwatch.GetElapsedTime(pending.FirstSeenTicks) <= PendingStaleTimeout;

            if (!inHold && !inFlight)
            {
                (holes ??= new List<uint>()).Add(seq);
            }
        }

        if (holes is null || holes.Count == 0)
        {
            return;
        }

        // A retransmission is only worth its round trip while the repair can still
        // arrive before the frames queued behind the hole expire (HoleTimeout);
        // beyond that crossover the receiver asks for a fresh keyframe instead.
        if (_smoothedRepairRttMs >= HoleTimeout.TotalMilliseconds)
        {
            SendRequest(new List<uint>(), isNack: false);
            return;
        }

        // Keyframe retransmissions burst ~10 packets and lose value with distance:
        // above KeyframeNackRttLimit a keyframe hole is recovered with a fresh
        // keyframe, while small delta frames stay worth retransmitting.
        if (_smoothedRepairRttMs >= KeyframeNackRttLimit.TotalMilliseconds)
        {
            List<uint> deltaHoles = new();

            foreach (uint seq in holes)
            {
                if (!_pending.TryGetValue(seq, out PendingFrame? hole) || hole.FrameType != FrameType.Keyframe)
                {
                    deltaHoles.Add(seq);
                }
            }

            SendRequest(new List<uint>(), isNack: false);

            if (deltaHoles.Count > 0)
            {
                SendRequest(deltaHoles, isNack: true);
            }

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
                _repairProbeTicks.Remove(seq);
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
        // pace each request type no faster than one round trip: NACKs or PLIs sent
        // before the previous repair could arrive would only duplicate work and bandwidth
        double intervalMs = Math.Max(RequestInterval.TotalMilliseconds, _smoothedRepairRttMs);
        long now = Stopwatch.GetTimestamp();
        long lastTicks = isNack ? _lastNackTicks : _lastPliTicks;

        if (Stopwatch.GetElapsedTime(lastTicks) < TimeSpan.FromMilliseconds(intervalMs))
        {
            return;
        }

        if (isNack)
        {
            _lastNackTicks = now;
            NackCount++;

            foreach (uint seq in missingSequences)
            {
                // first NACK for a sequence starts the probe (Karn-style: re-requests
                // of the same hole would make the sample ambiguous, so they are ignored)
                _repairProbeTicks.TryAdd(seq, now);
            }
        }
        else
        {
            _lastPliTicks = now;
            KeyframeRequestCount++;
            _pliSentTicks = now;
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
