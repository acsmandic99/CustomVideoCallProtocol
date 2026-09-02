using OpenCvSharp;
using System.Runtime.InteropServices;
using VideoCall.Protocol.Enums;

namespace VideoCall.Codecs.OpenCv;

public sealed class JpegVideoEncoder : IVideoEncoder
{
    private const int JpegQuality = 70;

    public (byte[] Data, FrameType FrameType) Encode(VideoFrame frame)
    {
        int expectedLength = frame.Width * frame.Height * 3;

        if (frame.Bgr24Data.Length < expectedLength)
        {
            throw new ArgumentException($"Frame buffer is {frame.Bgr24Data.Length} bytes, expected {expectedLength} for {frame.Width}x{frame.Height} BGR24.");
        }

        using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        Marshal.Copy(frame.Bgr24Data, 0, mat.Data, expectedLength);
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
