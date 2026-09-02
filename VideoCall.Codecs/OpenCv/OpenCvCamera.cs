using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoCall.Codecs.OpenCv;

public sealed class OpenCvCamera : ICamera
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<VideoFrame>? FrameCaptured;
    public event Action<string>? Failed;

    public void Start(int width, int height, int fps)
    {
        if (_cts is not null)
        {
            return;
        }

        VideoCapture? capture = FindUsableCapture();

        if (capture is null)
        {
            Failed?.Invoke("No usable camera found (tried indexes 0-4). If using DroidCam, start the DroidCam client and connect your phone first.");
            return;
        }

        capture.Set(VideoCaptureProperties.FrameWidth, width);
        capture.Set(VideoCaptureProperties.FrameHeight, height);
        capture.Set(VideoCaptureProperties.Fps, fps);

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
        // the loop owns the capture: disposing it here avoids a use-after-free
        // when Stop() cancels while a native read is still in progress
        try
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

                using Mat? converted = ToBgr24(frame);

                if (converted is null)
                {
                    continue;
                }

                var data = new byte[converted.Rows * converted.Cols * converted.ElemSize()];
                Marshal.Copy(converted.Data, data, 0, data.Length);

                FrameCaptured?.Invoke(new VideoFrame(data, converted.Cols, converted.Rows));
            }
        }
        finally
        {
            capture.Dispose();
        }
    }

    /// <summary>Normalizes whatever the driver delivers (gray, BGRA, BGR) into a BGR24 Mat.</summary>
    private static Mat? ToBgr24(Mat frame)
    {
        if (frame.Type() == MatType.CV_8UC3)
        {
            return frame.Clone();
        }

        if (frame.Type() == MatType.CV_8UC1)
        {
            return frame.CvtColor(ColorConversionCodes.GRAY2BGR);
        }

        if (frame.Type() == MatType.CV_8UC4)
        {
            return frame.CvtColor(ColorConversionCodes.BGRA2BGR);
        }

        return null;
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
