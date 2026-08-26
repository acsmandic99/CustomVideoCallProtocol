using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VideoCall.Codecs.OpenCv;

public sealed class FileVideoSource : ICamera
{
    private readonly string _path;
    private readonly TaskCompletionSource<bool> _audioReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _audioChunksSent;
    private long _audioDurationMs;
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
            _audioReady.TrySetResult(false);
            return;
        }

        Volatile.Write(ref _audioDurationMs, (long)reader.TotalTime.TotalMilliseconds);

        var chunk = new byte[320];
        long period = Stopwatch.Frequency / 50;
        long next = Stopwatch.GetTimestamp() + period;
        bool signaled = false;

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
                    if (!signaled)
                    {
                        signaled = true;
                        _audioReady.TrySetResult(true);
                    }

                    AudioCaptured?.Invoke((byte[])chunk.Clone());
                    Interlocked.Increment(ref _audioChunksSent);
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

        try
        {
            _audioReady.Task.Wait(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            capture.Dispose();
            return;
        }
        catch (AggregateException)
        {
        }

        bool audioMaster = _audioReady.Task.IsCompleted && _audioReady.Task.Result;

        if (audioMaster)
        {
            long audioDurationMs = Volatile.Read(ref _audioDurationMs);
            long frameCount = (long)capture.FrameCount;

            if (audioDurationMs > 0 && frameCount > 0)
            {
                fps = (int)Math.Clamp(Math.Round(frameCount * 1000.0 / audioDurationMs), 1, 60);
            }
        }

        long period = Stopwatch.Frequency / fps;
        long next = Stopwatch.GetTimestamp();
        long frameIndex = 0;

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
                frameIndex = audioMaster ? Interlocked.Read(ref _audioChunksSent) * 20L * fps / 1000 + 1 : 0;
                continue;
            }

            var data = new byte[frame.Rows * frame.Cols * frame.ElemSize()];
            Marshal.Copy(frame.Data, data, 0, data.Length);
            FrameCaptured?.Invoke(new VideoFrame(data, frame.Cols, frame.Rows));
            frameIndex++;

            if (audioMaster)
            {
                WaitForAudioSlot(frameIndex, fps, cancellationToken);
                continue;
            }

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

    private void WaitForAudioSlot(long frameIndex, int fps, CancellationToken cancellationToken)
    {
        long lastChunks = -1;
        long lastChange = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            long chunks = Interlocked.Read(ref _audioChunksSent);

            if (chunks > 0 && chunks * 20L * fps >= frameIndex * 1000L)
            {
                return;
            }

            if (chunks != lastChunks)
            {
                lastChunks = chunks;
                lastChange = Stopwatch.GetTimestamp();
            }
            else if (Stopwatch.GetElapsedTime(lastChange) > TimeSpan.FromSeconds(3))
            {
                return;
            }

            Thread.Sleep(1);
        }
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
