using VideoCall.Protocol.Enums;

namespace VideoCall.Media;

public interface IFrameSink
{
    void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec);
}
