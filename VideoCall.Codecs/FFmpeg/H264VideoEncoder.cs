using System.Runtime.InteropServices;
using OpenCvSharp;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using VideoCall.Protocol.Enums;

namespace VideoCall.Codecs.FFmpeg;

public sealed class H264VideoEncoder : IVideoEncoder
{
    private const int GopSize = 60;
    private const int Bitrate = 1_500_000;
    private const int KeyPacketFlag = 0x1;

    private readonly CodecContext _context;
    private readonly Frame _frame;
    private readonly Packet _packet;
    private readonly Mat _i420;
    private readonly int _width;
    private readonly int _height;
    private long _pts;
    private bool _forceKeyframe;
    private bool _disposed;

    public H264VideoEncoder(int width, int height, int fps = 30)
    {
        _width = width;
        _height = height;

        Codec codec = Codec.CommonEncoders.Libx264;
        _context = new CodecContext(codec)
        {
            Width = width,
            Height = height,
            PixelFormat = AVPixelFormat.Yuv420p,
            TimeBase = new AVRational(1, fps),
            Framerate = new AVRational(fps, 1),
            BitRate = Bitrate,
            GopSize = GopSize,
            MaxBFrames = 0,
        };
        _context.Open(codec, new MediaDictionary
        {
            ["preset"] = "ultrafast",
            ["tune"] = "zerolatency",
            ["forced-idr"] = "1",
        });

        _frame = new Frame
        {
            Width = width,
            Height = height,
            Format = (int)AVPixelFormat.Yuv420p,
        };
        _packet = new Packet();
        _i420 = new Mat(height * 3 / 2, width, MatType.CV_8UC1);
    }

    public void ForceKeyframe()
    {
        _forceKeyframe = true;
    }

    public (byte[] Data, FrameType FrameType) Encode(VideoFrame frame)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(H264VideoEncoder));
        }

        using var bgr = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        Marshal.Copy(frame.Bgr24Data, 0, bgr.Data, frame.Bgr24Data.Length);
        Cv2.CvtColor(bgr, _i420, ColorConversionCodes.BGR2YUV_I420);

        long ySize = (long)_width * _height;
        long uSize = ySize / 4;
        IntPtr basePtr = _i420.Data;

        _frame.Data[0] = basePtr;
        _frame.Data[1] = (nint)(basePtr + ySize);
        _frame.Data[2] = (nint)(basePtr + ySize + uSize);
        _frame.Linesize[0] = _width;
        _frame.Linesize[1] = _width / 2;
        _frame.Linesize[2] = _width / 2;
        _frame.Pts = _pts++;

        if (_forceKeyframe)
        {
            _forceKeyframe = false;
            _frame.KeyFrame = 1;
        }

        _context.SendFrame(_frame);

        using var output = new MemoryStream();
        bool keyframe = false;

        while (_context.ReceivePacket(_packet) == CodecResult.Success)
        {
            keyframe |= (_packet.Flags & KeyPacketFlag) != 0;

            int size = _packet.Data.Length;
            var buffer = new byte[size];
            Marshal.Copy(_packet.Data.Pointer, buffer, 0, size);
            output.Write(buffer, 0, buffer.Length);

            _packet.Unref();
        }

        return (output.ToArray(), keyframe ? FrameType.Keyframe : FrameType.Delta);
    }

    public void Dispose()
    {
        _disposed = true;
        _context.Dispose();
        _frame.Dispose();
        _packet.Dispose();
        _i420.Dispose();
    }
}
