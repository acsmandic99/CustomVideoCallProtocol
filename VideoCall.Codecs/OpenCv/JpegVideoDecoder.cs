using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoCall.Codecs.OpenCv;

public sealed class JpegVideoDecoder : IVideoDecoder
{
    public VideoFrame? Decode(byte[] data)
    {
        try
        {
            using var mat = Cv2.ImDecode(data, ImreadModes.Color);

            if (mat.Empty())
            {
                return null;
            }

            var bgr = new byte[mat.Rows * mat.Cols * mat.ElemSize()];
            Marshal.Copy(mat.Data, bgr, 0, bgr.Length);

            return new VideoFrame(bgr, mat.Cols, mat.Rows);
        }
        catch (OpenCvSharpException)
        {
            return null;
        }
    }

    public void Dispose()
    {
    }
}
