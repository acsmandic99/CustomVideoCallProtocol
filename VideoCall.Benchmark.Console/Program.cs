using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VideoCall.Codecs;
using VideoCall.Codecs.FFmpeg;
using VideoCall.Codecs.OpenCv;
using VideoCall.Media;
using VideoCall.Media.Testing;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;

namespace VideoCall.Benchmark.Console;

public static class Program
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Fps = 30;
    private const int FrameCount = 300;
    private static readonly int[] LossLevels = { 0, 5, 10, 15, 20, 30, 50 };

    public static async Task Main()
    {
        System.Console.WriteLine("=== VideoCall Protocol Benchmark ===");
        System.Console.WriteLine($"frames={FrameCount}, {Fps}fps, {Width}x{Height}, synthetic moving pattern");
        System.Console.WriteLine();

        System.Console.WriteLine("codec  loss  sent  delivered  delivered%  avgKB  nack  pli");
        System.Console.WriteLine("----   ----   ----  --------   ----------  -----  ----  ---");

        foreach (VideoCodec codec in new[] { VideoCodec.H264, VideoCodec.Jpeg })
        {
            foreach (int loss in LossLevels)
            {
                UdpResult r = await RunUdpAsync(codec, loss);
                System.Console.WriteLine($"{codec,-6} {loss,3}%  {r.Sent,4}  {r.Delivered,8}  {r.DeliveredPercent,9:F1}%  {r.AvgFrameKB,5:F1}  {r.NackCount,4}  {r.PliCount,3}");
            }
        }

        System.Console.WriteLine();
        System.Console.WriteLine("--- TCP baseline (no loss simulation possible at app level) ---");
        System.Console.WriteLine("codec  sent  delivered  avgKB  avgLatencyMs");

        foreach (VideoCodec codec in new[] { VideoCodec.H264, VideoCodec.Jpeg })
        {
            TcpResult t = await RunTcpAsync(codec);
            System.Console.WriteLine($"{codec,-6} {t.Sent,4}  {t.Delivered,8}  {t.AvgFrameKB,5:F1}  {t.AvgLatencyMs,10:F1}");
        }

        System.Console.WriteLine();
        System.Console.WriteLine("=== Benchmark complete ===");
    }

    private sealed record UdpResult(int Sent, int Delivered, double DeliveredPercent, double AvgFrameKB, int NackCount, int PliCount);
    private sealed record TcpResult(int Sent, int Delivered, double AvgFrameKB, double AvgLatencyMs);

    private static byte[] RenderFrame(int t)
    {
        var bgr = new byte[Width * Height * 3];

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int i = (row + x) * 3;
                bgr[i] = (byte)((x + t) & 0xFF);
                bgr[i + 1] = (byte)((y + t) & 0xFF);
                bgr[i + 2] = (byte)((x + y) & 0xFF);
            }
        }

        int boxSize = 120;
        int bx = (t * 6) % (Width - boxSize);
        int by = (t * 4) % (Height - boxSize);

        for (int y = by; y < by + boxSize; y++)
        {
            int row = y * Width;
            for (int x = bx; x < bx + boxSize; x++)
            {
                int i = (row + x) * 3;
                bgr[i] = 255;
                bgr[i + 1] = 60;
                bgr[i + 2] = 60;
            }
        }

        return bgr;
    }

    private static (IVideoEncoder Encoder, Func<byte[], (byte[], FrameType)> Encode) MakeEncoder(VideoCodec codec)
    {
        if (codec == VideoCodec.H264)
        {
            var enc = new H264VideoEncoder(Width, Height, Fps);
            return (enc, data => enc.Encode(new VideoFrame(data, Width, Height)));
        }

        var jpeg = new JpegVideoEncoder();
        return (jpeg, data => jpeg.Encode(new VideoFrame(data, Width, Height)));
    }

    private static async Task<UdpResult> RunUdpAsync(VideoCodec codec, int lossPercent)
    {
        var counter = new CountingSink();
        var senderTransport = new LossyTransportDecorator(new UdpMediaTransport(), lossPercent, seed: 11);
        var receiverTransport = new UdpMediaTransport();

        ushort senderPort = (ushort)Random.Shared.Next(21000, 22000);
        ushort receiverPort = (ushort)Random.Shared.Next(22100, 23000);

        using var sender = new MediaSession(senderTransport, new IPEndPoint(IPAddress.Loopback, receiverPort), new NullSink());
        using var receiver = new MediaSession(receiverTransport, new IPEndPoint(IPAddress.Loopback, senderPort), counter);
        sender.Start(senderPort);
        receiver.Start(receiverPort);

        (IVideoEncoder encoder, var encode) = MakeEncoder(codec);
        long totalBytes = 0;

        for (int i = 0; i < FrameCount; i++)
        {
            byte[] bgr = RenderFrame(i);
            (byte[] data, FrameType type) = encode(bgr);
            totalBytes += data.Length;
            sender.SendFrame(data, type, codec);
            await Task.Delay(1000 / Fps);
        }

        await Task.Delay(1500);
        encoder.Dispose();

        int delivered = counter.Count;
        double percent = 100.0 * delivered / FrameCount;
        double avgKb = totalBytes / 1024.0 / FrameCount;
        return new UdpResult(FrameCount, delivered, percent, avgKb, receiver.NackCount, receiver.KeyframeRequestCount);
    }

    private static async Task<TcpResult> RunTcpAsync(VideoCodec codec)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            var lengthHeader = new byte[4];
            int delivered = 0;
            var stopwatch = Stopwatch.StartNew();

            while (delivered < FrameCount)
            {
                int read = 0;
                while (read < 4)
                {
                    int n = await stream.ReadAsync(lengthHeader.AsMemory(read, 4 - read));
                    if (n == 0) return delivered;
                    read += n;
                }

                int len = BitConverter.ToInt32(lengthHeader);
                var payload = new byte[len];
                read = 0;
                while (read < len)
                {
                    read += await stream.ReadAsync(payload.AsMemory(read));
                }

                delivered++;
            }

            return delivered;
        });

        using TcpClient sender = new TcpClient();
        await sender.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream outStream = sender.GetStream();

        (IVideoEncoder encoder, var encode) = MakeEncoder(codec);
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < FrameCount; i++)
        {
            byte[] bgr = RenderFrame(i);
            (byte[] data, _) = encode(bgr);
            totalBytes += data.Length;
            await outStream.WriteAsync(BitConverter.GetBytes(data.Length));
            await outStream.WriteAsync(data);
            await Task.Delay(1000 / Fps);
        }

        int delivered = await serverTask;
        encoder.Dispose();
        listener.Stop();

        return new TcpResult(FrameCount, delivered, totalBytes / 1024.0 / FrameCount, sw.Elapsed.TotalMilliseconds / FrameCount);
    }

    private sealed class CountingSink : IFrameSink
    {
        public int Count { get; private set; }

        public void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec)
        {
            Count++;
        }
    }

    private sealed class NullSink : IFrameSink
    {
        public void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec)
        {
        }
    }
}
