namespace VideoCall.Codecs;

public sealed class SyntheticCamera : ICamera
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<VideoFrame>? FrameCaptured;

#pragma warning disable CS0067
    public event Action<string>? Failed;
#pragma warning restore CS0067

    public void Start(int width, int height, int fps)
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(width, height, fps, _cts.Token));
    }

    private void Loop(int width, int height, int fps, CancellationToken cancellationToken)
    {
        int frameInterval = 1000 / Math.Max(1, fps);
        var data = new byte[width * height * 3];
        int frameIndex = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            Render(width, height, data, frameIndex);
            FrameCaptured?.Invoke(new VideoFrame((byte[])data.Clone(), width, height));
            frameIndex++;
            Thread.Sleep(frameInterval);
        }
    }

    private static void Render(int width, int height, byte[] bgr, int t)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = (row + x) * 3;
                bgr[i] = (byte)((x + t) & 0xFF);
                bgr[i + 1] = (byte)((y + t) & 0xFF);
                bgr[i + 2] = (byte)((x + y) & 0xFF);
            }
        }

        int boxSize = Math.Min(120, width / 4);
        int bx = (t * 6) % Math.Max(1, width - boxSize);
        int by = (t * 4) % Math.Max(1, height - boxSize);

        for (int y = by; y < by + boxSize; y++)
        {
            int row = y * width;
            for (int x = bx; x < bx + boxSize; x++)
            {
                int i = (row + x) * 3;
                bgr[i] = 255;
                bgr[i + 1] = 60;
                bgr[i + 2] = 60;
            }
        }

        int barWidth = width / 30;
        int barIndex = t % 30;
        for (int y = 0; y < 20; y++)
        {
            int row = y * width;
            for (int x = barIndex * barWidth; x < (barIndex + 1) * barWidth && x < width; x++)
            {
                int i = (row + x) * 3;
                bgr[i] = 255;
                bgr[i + 1] = 255;
                bgr[i + 2] = 255;
            }
        }
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
