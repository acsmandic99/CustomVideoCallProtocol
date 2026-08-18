using NAudio.Wave;

namespace VideoCall.Codecs.Audio;

public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 8000;
    public const int Channels = 1;
    public const int Bits = 16;
    public const int ChunkMilliseconds = 20;

    private readonly WaveInEvent _waveIn;

    public event Action<byte[]>? ChunkCaptured;
    public event Action<string>? Failed;

    public AudioCapture(int deviceIndex = 0)
    {
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(SampleRate, Bits, Channels),
            BufferMilliseconds = ChunkMilliseconds,
        };
        _waveIn.DataAvailable += OnDataAvailable;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        ChunkCaptured?.Invoke(chunk);
    }

    public void Start()
    {
        try
        {
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"Microphone error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _waveIn.Dispose();
    }
}

public sealed class AudioPlayer : IDisposable
{
    private readonly BufferedWaveProvider _provider;
    private readonly WaveOutEvent _waveOut;

    public AudioPlayer()
    {
        _provider = new BufferedWaveProvider(new WaveFormat(AudioCapture.SampleRate, AudioCapture.Bits, AudioCapture.Channels))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_provider);
    }

    public void Play(byte[] chunk)
    {
        _provider.AddSamples(chunk, 0, chunk.Length);
    }

    public void Start()
    {
        _waveOut.Play();
    }

    public void Dispose()
    {
        _waveOut.Stop();
        _waveOut.Dispose();
    }
}
