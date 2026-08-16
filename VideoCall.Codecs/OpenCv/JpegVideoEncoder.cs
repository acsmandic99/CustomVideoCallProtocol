using OpenCvSharp;
using VideoCall.Protocol.Enums;

namespace VideoCall.Codecs.OpenCv;

public sealed class JpegVideoEncoder : IVideoEncoder
{
    private const int JpegQuality = 70;

    public (byte[] Data, FrameType FrameType) Encode(VideoFrame frame)
    {
        using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(frame.Bgr24Data, 0, mat.Data, frame.Bgr24Data.Length);
        var qualityParam = new ImageEncodingParam(ImwriteFlags.JpegQuality, JpegQuality);
        Cv2.ImEncode(".jpg", mat, out byte[] jpeg, qualityParam);
        return (jpeg, FrameType.Keyframe);
    }

    public void ForceKeyframe()
    {
    }

    public void Dispose()
    {
    }
}
