using VideoCall.Protocol.Enums;

namespace VideoCall.Codecs;

public interface IVideoEncoder : IDisposable
{
    (byte[] Data, FrameType FrameType) Encode(VideoFrame frame);

    void ForceKeyframe();
}
