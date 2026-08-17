using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoCall.Codecs.OpenCv;

public sealed class FileVideoSource : ICamera
{
    private readonly string _path;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<VideoFrame>? FrameCaptured;
    public event Action<string>? Failed;

    public FileVideoSource(string path)
    {
        _path = path;
    }

    public static (int Width, int Height, int Fps)? Probe(string path)
    {
        using var capture = new VideoCapture(path);

        if (!capture.IsOpened())
        {
            return null;
        }

        int fps = (int)Math.Round(Math.Max(1, capture.Fps));
        return ((int)capture.FrameWidth, (int)capture.FrameHeight, fps);
    }

    public void Start(int width, int height, int fps)
    {
        var capture = new VideoCapture(_path);

        if (!capture.IsOpened())
        {
            capture.Dispose();
            Failed?.Invoke($"Cannot open video file: {_path}");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(capture, _cts.Token));
    }

    private void Loop(VideoCapture capture, CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        int delay = (int)(1000 / Math.Max(1, Math.Round(capture.Fps)));

        while (!cancellationToken.IsCancellationRequested)
        {
            bool read = false;

            try
            {
                read = capture.Read(frame);
            }
            catch (Exception)
            {
                break;
            }

            if (!read || frame.Empty())
            {
                capture.Set(VideoCaptureProperties.PosFrames, 0);
                Thread.Sleep(10);
                continue;
            }

            var data = new byte[frame.Rows * frame.Cols * frame.ElemSize()];
            Marshal.Copy(frame.Data, data, 0, data.Length);
            FrameCaptured?.Invoke(new VideoFrame(data, frame.Cols, frame.Rows));
            Thread.Sleep(delay);
        }

        capture.Dispose();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _loopTask?.Wait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
