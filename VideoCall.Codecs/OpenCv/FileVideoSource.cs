using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoCall.Codecs.OpenCv;

public sealed class FileVideoSource : ICamera
{
    private readonly string _path;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _audioTask;

    public event Action<VideoFrame>? FrameCaptured;
    public event Action<byte[]>? AudioCaptured;
    public event Action? AudioUnavailable;
    public event Action<string>? Failed;

    public FileVideoSource(string path)
    {
        _path = path;
    }

    public static (int Width, int Height, int Fps)? Probe(string path)
    {
        using var capture = new VideoCapture(path, VideoCaptureAPIs.FFMPEG);

        if (!capture.IsOpened())
        {
            return null;
        }

        int fps = (int)Math.Min(60, Math.Round(Math.Max(1, capture.Fps)));
        return ((int)capture.FrameWidth, (int)capture.FrameHeight, fps);
    }

    public void Start(int width, int height, int fps)
    {
        var capture = new VideoCapture(_path, VideoCaptureAPIs.FFMPEG);

        if (!capture.IsOpened())
        {
            capture.Dispose();
            Failed?.Invoke($"Cannot open video file: {_path}");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(capture, _cts.Token));
        _audioTask = Task.Run(() => AudioLoop(_cts.Token));
    }

    private void AudioLoop(CancellationToken ct)
    {
        NAudio.Wave.MediaFoundationReader? reader = null;
        NAudio.Wave.MediaFoundationResampler? resampler = null;

        try
        {
            reader = new NAudio.Wave.MediaFoundationReader(_path);
            resampler = new NAudio.Wave.MediaFoundationResampler(reader, new NAudio.Wave.WaveFormat(8000, 16, 1));
        }
        catch
        {
            AudioUnavailable?.Invoke();
            return;
        }

        var chunk = new byte[320];
        long period = Stopwatch.Frequency / 50;
        long next = Stopwatch.GetTimestamp() + period;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = 0;

                while (read < chunk.Length)
                {
                    int n = resampler.Read(chunk, read, chunk.Length - read);

                    if (n == 0)
                    {
                        reader.Position = 0;
                        break;
                    }

                    read += n;
                }

                if (read == chunk.Length)
                {
                    AudioCaptured?.Invoke((byte[])chunk.Clone());
                }

                long sleepTicks = next - Stopwatch.GetTimestamp();

                if (sleepTicks > 0)
                {
                    Thread.Sleep((int)(sleepTicks * 1000 / Stopwatch.Frequency));
                }
                else
                {
                    next = Stopwatch.GetTimestamp();
                }

                next += period;
            }
        }
        catch
        {
        }
        finally
        {
            resampler?.Dispose();
            reader?.Dispose();
        }
    }

    private void Loop(VideoCapture capture, CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        int fps = (int)Math.Min(60, Math.Max(1, Math.Round(capture.Fps)));
        long period = Stopwatch.Frequency / fps;
        long next = Stopwatch.GetTimestamp();

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
                next = Stopwatch.GetTimestamp();
                continue;
            }

            var data = new byte[frame.Rows * frame.Cols * frame.ElemSize()];
            Marshal.Copy(frame.Data, data, 0, data.Length);
            FrameCaptured?.Invoke(new VideoFrame(data, frame.Cols, frame.Rows));

            next += period;
            long sleepTicks = next - Stopwatch.GetTimestamp();

            if (sleepTicks > 0)
            {
                Thread.Sleep((int)(sleepTicks * 1000 / Stopwatch.Frequency));
            }
            else
            {
                next = Stopwatch.GetTimestamp();
            }
        }

        capture.Dispose();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _loopTask?.Wait(TimeSpan.FromSeconds(2));
        _audioTask?.Wait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
