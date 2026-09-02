namespace VideoCall.Codecs;

public interface IVideoDecoder : IDisposable
{
    VideoFrame? Decode(byte[] data);
}
