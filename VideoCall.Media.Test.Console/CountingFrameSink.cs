using VideoCall.Media;
using VideoCall.Protocol.Enums;

namespace VideoCall.Media.Test.Console;

public sealed class CountingFrameSink : IFrameSink
{
    private readonly object _lock = new();
    private readonly string _name;
    private readonly bool _verbose;

    public int ReceivedCount { get; private set; }
    public int KeyframeCount { get; private set; }

    public CountingFrameSink(string name, bool verbose)
    {
        _name = name;
        _verbose = verbose;
    }

    public void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec)
    {
        lock (_lock)
        {
            ReceivedCount++;
            if (frameType == FrameType.Keyframe)
            {
                KeyframeCount++;
            }

            if (_verbose)
            {
                System.Console.WriteLine($"  [{_name}] <- frame seq={sequence} type={frameType} codec={videoCodec} bytes={data.Length}");
            }
        }
    }
}

public sealed class NullFrameSink : IFrameSink
{
    public void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec)
    {
    }
}
