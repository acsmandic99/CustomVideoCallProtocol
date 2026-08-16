namespace VideoCall.Codecs;

public interface ICamera : IDisposable
{
    event Action<VideoFrame>? FrameCaptured;
    event Action<string>? Failed;

    void Start(int width, int height, int fps);

    void Stop();
}
