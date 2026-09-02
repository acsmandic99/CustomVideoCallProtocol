using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoCall.Benchmark.Core;

/// <summary>
/// Synchronous deterministic frame reader for a video file: every run of the same file
/// produces the exact same frame sequence (looping from the start when the file ends).
/// </summary>
public sealed class FileFrameProvider : IDisposable
{
    private readonly VideoCapture _capture;
    private readonly string _path;
    private readonly Mat _mat = new();

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    private FileFrameProvider(VideoCapture capture, string path)
    {
        _capture = capture;
        _path = path;
        Width = capture.FrameWidth;
        Height = capture.FrameHeight;
        Fps = (int)Math.Clamp(Math.Round(Math.Max(1, capture.Fps)), 1, 60);
    }

    public static FileFrameProvider? TryOpen(string path)
    {
        var capture = new VideoCapture(path, VideoCaptureAPIs.FFMPEG);

        if (!capture.IsOpened())
        {
            capture.Dispose();
            return null;
        }

        return new FileFrameProvider(capture, path);
    }

    public byte[] ReadFrame()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool read = _capture.Read(_mat);

            if (read && !_mat.Empty())
            {
                var data = new byte[_mat.Rows * _mat.Cols * _mat.ElemSize()];
                Marshal.Copy(_mat.Data, data, 0, data.Length);
                return data;
            }

            _capture.Set(VideoCaptureProperties.PosFrames, 0);
        }

        throw new InvalidOperationException($"Video file cannot be read: {_path}");
    }

    public void Dispose()
    {
        _mat.Dispose();
        _capture.Dispose();
    }
}
