using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoCall.Codecs.OpenCv;

public sealed class OpenCvCamera : ICamera
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<VideoFrame>? FrameCaptured;
    public event Action<string>? Failed;

    public void Start(int width, int height, int fps)
    {
        VideoCapture? capture = FindUsableCapture();

        if (capture is null)
        {
            Failed?.Invoke("No usable camera found (tried indexes 0-4). If using DroidCam, start the DroidCam client and connect your phone first.");
            return;
        }

        capture.Set(VideoCaptureProperties.FrameWidth, width);
        capture.Set(VideoCaptureProperties.FrameHeight, height);
        capture.Set(VideoCaptureProperties.Fps, fps);

        _capture = capture;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => CaptureLoop(capture, _cts.Token));
    }

    private static VideoCapture? FindUsableCapture()
    {
        for (int i = 0; i < 5; i++)
        {
            var candidate = new VideoCapture(i, VideoCaptureAPIs.DSHOW);

            if (!candidate.IsOpened())
            {
                candidate.Dispose();
                continue;
            }

            using var probe = new Mat();
            bool hasFrames = false;

            try
            {
                hasFrames = candidate.Read(probe) && !probe.Empty();
            }
            catch (Exception)
            {
                hasFrames = false;
            }

            if (hasFrames)
            {
                return candidate;
            }

            candidate.Dispose();
        }

        return null;
    }

    private void CaptureLoop(VideoCapture capture, CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        var lastFrameTime = Stopwatch.GetTimestamp();
        bool failureReported = false;

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
                if (!failureReported && Stopwatch.GetElapsedTime(lastFrameTime) > FrameTimeout)
                {
                    failureReported = true;
                    Failed?.Invoke("Camera opened but produced no frames for 3 seconds.");
                    break;
                }

                Thread.Sleep(5);
                continue;
            }

            lastFrameTime = Stopwatch.GetTimestamp();

            var data = new byte[frame.Rows * frame.Cols * frame.ElemSize()];
            Marshal.Copy(frame.Data, data, 0, data.Length);

            FrameCaptured?.Invoke(new VideoFrame(data, frame.Cols, frame.Rows));
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _loopTask?.Wait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
