using VideoCall.Protocol.Enums;

namespace VideoCall.Codecs;

public interface IVideoDecoder : IDisposable
{
    VideoFrame? Decode(byte[] data, FrameType frameType);
}
