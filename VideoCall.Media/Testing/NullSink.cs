using VideoCall.Protocol.Enums;

namespace VideoCall.Media.Testing;

public sealed class NullSink : IFrameSink
{
    public void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec)
    {
    }
}
