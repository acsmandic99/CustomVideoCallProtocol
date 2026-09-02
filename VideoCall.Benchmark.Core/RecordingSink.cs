using System.Diagnostics;
using VideoCall.Media;
using VideoCall.Protocol.Enums;

namespace VideoCall.Benchmark.Core;

/// <summary>
/// Thread-safe receiver-side recorder: first delivery time and payload size per video frame sequence.
/// </summary>
public sealed class RecordingSink : IFrameSink
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, (long Tick, long Bytes)> _delivered = new();
    private uint _maxSequence;
    private int _outOfOrder;
    private uint _nextExpected = 1;
    private bool _haveReference;
    private int _decodable;

    public int DeliveredCount
    {
        get
        {
            lock (_lock)
            {
                return _delivered.Count;
            }
        }
    }

    public int DecodableCount
    {
        get
        {
            lock (_lock)
            {
                return _decodable;
            }
        }
    }

    public long DeliveredBytes
    {
        get
        {
            lock (_lock)
            {
                return _delivered.Values.Sum(v => v.Bytes);
            }
        }
    }

    public int OutOfOrderCount
    {
        get
        {
            lock (_lock)
            {
                return _outOfOrder;
            }
        }
    }

    public void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec)
    {
        if (frameType == FrameType.Audio)
        {
            return;
        }

        Record(sequence, data.Length, Stopwatch.GetTimestamp(), frameType == FrameType.Keyframe);
    }

    public void Record(uint sequence, long bytes, long tick, bool isKeyframe = false)
    {
        lock (_lock)
        {
            if (_delivered.ContainsKey(sequence))
            {
                return;
            }

            _delivered[sequence] = (tick, bytes);

            if (sequence < _maxSequence)
            {
                _outOfOrder++;
            }

            _maxSequence = Math.Max(_maxSequence, sequence);

            // A delivered frame is only renderable when every frame since the last
            // delivered keyframe arrived before it (H.264 inter prediction).
            if (isKeyframe)
            {
                _haveReference = true;
                _decodable++;
                _nextExpected = sequence + 1;
            }
            else if (_haveReference && sequence == _nextExpected)
            {
                _decodable++;
                _nextExpected = sequence + 1;
            }
            else if (!_haveReference && sequence == 1)
            {
                _haveReference = true;
                _decodable++;
                _nextExpected = 2;
            }
        }
    }

    public (long Tick, long Bytes)? GetDelivery(uint sequence)
    {
        lock (_lock)
        {
            return _delivered.TryGetValue(sequence, out (long Tick, long Bytes) value) ? value : null;
        }
    }

    public List<long> DeliveredTicksSorted()
    {
        lock (_lock)
        {
            return _delivered.Values.Select(v => v.Tick).OrderBy(t => t).ToList();
        }
    }
}
