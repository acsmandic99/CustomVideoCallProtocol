using System.Runtime.InteropServices;
using OpenCvSharp;
using VideoCall.Protocol.Enums;

namespace VideoCall.Codecs.OpenCv;

public sealed class JpegVideoDecoder : IVideoDecoder
{
    public VideoFrame Decode(byte[] data, FrameType frameType)
    {
        using var mat = Cv2.ImDecode(data, ImreadModes.Color);

        if (mat.Empty())
        {
            throw new InvalidOperationException("Failed to decode JPEG frame");
        }

        var bgr = new byte[mat.Rows * mat.Cols * mat.ElemSize()];
        Marshal.Copy(mat.Data, bgr, 0, bgr.Length);

        return new VideoFrame(bgr, mat.Cols, mat.Rows);
    }

    public void Dispose()
    {
    }
}
