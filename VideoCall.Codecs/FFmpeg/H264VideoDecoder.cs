using System.Runtime.InteropServices;
using OpenCvSharp;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using VideoCall.Protocol.Enums;

namespace VideoCall.Codecs.FFmpeg;

public sealed class H264VideoDecoder : IVideoDecoder
{
    private readonly CodecContext _context;
    private readonly Frame _frame;
    private readonly Packet _packet;
    private bool _disposed;

    public H264VideoDecoder()
    {
        Codec codec = Codec.FindDecoderById(AVCodecID.H264);
        _context = new CodecContext(codec);
        _context.Open(codec);
        _frame = new Frame();
        _packet = new Packet();
    }

    public VideoFrame? Decode(byte[] data, FrameType frameType)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(H264VideoDecoder));
        }

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            _packet.Data = new DataPointer(handle.AddrOfPinnedObject(), data.Length);
            _context.SendPacket(_packet);
        }
        finally
        {
            handle.Free();
        }

        if (_context.ReceiveFrame(_frame) != CodecResult.Success)
        {
            return null;
        }

        try
        {
            int width = _frame.Width;
            int height = _frame.Height;

            using var i420 = new Mat(height * 3 / 2, width, MatType.CV_8UC1);
            long ySize = (long)width * height;
            long uSize = ySize / 4;

            CopyPlane(_frame.Data[0], _frame.Linesize[0], i420.Data, width, height, width);
            CopyPlane(_frame.Data[1], _frame.Linesize[1], (nint)(i420.Data + ySize), width / 2, height / 2, width / 2);
            CopyPlane(_frame.Data[2], _frame.Linesize[2], (nint)(i420.Data + ySize + uSize), width / 2, height / 2, width / 2);

            using var bgr = new Mat();
            Cv2.CvtColor(i420, bgr, ColorConversionCodes.YUV2BGR_I420);

            var bgrData = new byte[bgr.Rows * bgr.Cols * bgr.ElemSize()];
            Marshal.Copy(bgr.Data, bgrData, 0, bgrData.Length);

            return new VideoFrame(bgrData, width, height);
        }
        finally
        {
            _frame.Unref();
        }
    }

    private static unsafe void CopyPlane(IntPtr source, int sourceStride, IntPtr destination, int destinationStride, int rows, int cols)
    {
        byte* src = (byte*)source;
        byte* dst = (byte*)destination;

        for (int y = 0; y < rows; y++)
        {
            Buffer.MemoryCopy(src + (long)y * sourceStride, dst + (long)y * destinationStride, cols, cols);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _context.Dispose();
        _frame.Dispose();
        _packet.Dispose();
    }
}
