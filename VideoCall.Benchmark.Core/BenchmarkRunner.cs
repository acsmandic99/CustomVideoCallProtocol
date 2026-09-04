using System.Diagnostics;
using System.Net;
using VideoCall.Codecs;
using VideoCall.Codecs.FFmpeg;
using VideoCall.Codecs.OpenCv;
using VideoCall.Media;
using VideoCall.Media.Testing;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;

namespace VideoCall.Benchmark.Core;

public sealed class BenchmarkRunner
{
    private sealed class Accumulator
    {
        public readonly Dictionary<uint, long> SendTicks = new();
        public readonly List<(uint Sequence, long Bytes, string FrameType, double EncodeMs)> Frames = new();
        public readonly List<TimeseriesPoint> Timeseries = new();
        public long TotalBytes;
    }

    private sealed record PreparedFrame(uint Sequence, byte[] Encoded, FrameType FrameType, double EncodeMs, long ProduceTick);

    public static async Task<BenchmarkResult> RunAsync(
        BenchmarkConfig config,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"Opening video file: {Path.GetFileName(config.VideoFilePath)}");

        using FileFrameProvider? provider = FileFrameProvider.TryOpen(config.VideoFilePath);

        if (provider is null)
        {
            throw new InvalidOperationException($"Cannot open video file: {config.VideoFilePath}");
        }

        progress?.Report($"Source {provider.Width}x{provider.Height}@{provider.Fps}fps · {config.Transport.DisplayName()} · {config.Codec} · loss {config.LossPercent}% · delay {config.DelayMs} ms · {config.DurationSeconds}s");

        using IVideoEncoder encoder = config.Codec == VideoCodec.H264
            ? new H264VideoEncoder(provider.Width, provider.Height, provider.Fps)
            : new JpegVideoEncoder();

        var sink = new RecordingSink();
        var accumulator = new Accumulator();
        double elapsed;

        int nackCount;
        int pliCount;
        int retransmitted;
        int droppedDatagrams;
        double repairRttMs;
        Func<(int Nack, int Pli)>? sampleRecovery = null;

        if (config.Transport == TransportKind.Tcp)
        {
            using var channel = await TcpFrameChannel.ConnectAsync(
                (sequence, bytes, tick) => sink.Record(sequence, bytes, tick));

            elapsed = await SendLoopAsync(provider, encoder, config, frame =>
            {
                accumulator.SendTicks[frame.Sequence] = frame.ProduceTick;
                accumulator.TotalBytes += frame.Encoded.Length;
                accumulator.Frames.Add((frame.Sequence, frame.Encoded.Length, frame.FrameType.ToString(), frame.EncodeMs));
                return channel.SendFrameAsync(frame.Encoded);
            }, sink, accumulator, null, progress, cancellationToken);
            await Task.Delay(500, CancellationToken.None);
            channel.CloseSender();
            await Task.Delay(500, CancellationToken.None);

            nackCount = 0;
            pliCount = 0;
            retransmitted = 0;
            droppedDatagrams = 0;
            repairRttMs = 0;
        }
        else
        {
            bool recovery = config.Transport == TransportKind.CustomUdp;

            var lossySenderTransport = new LossyTransportDecorator(new UdpMediaTransport(), config.LossPercent, config.Seed);
            IUdpMediaTransport senderTransport = lossySenderTransport;
            IUdpMediaTransport receiverTransport = new UdpMediaTransport();

            if (config.DelayMs > 0)
            {
                // symmetric one-way delay on both endpoints: a recovery round trip takes ~2x delay
                senderTransport = new DelayTransportDecorator(senderTransport, config.DelayMs);
                receiverTransport = new DelayTransportDecorator(receiverTransport, config.DelayMs);
            }

            ushort senderPort = (ushort)Random.Shared.Next(24000, 24900);
            ushort receiverPort = (ushort)Random.Shared.Next(24901, 25400);

            using var senderSession = new MediaSession(senderTransport, new IPEndPoint(IPAddress.Loopback, receiverPort), new NullSink(), recovery);
            using var receiverSession = new MediaSession(receiverTransport, new IPEndPoint(IPAddress.Loopback, senderPort), sink, recovery);
            senderSession.Start(senderPort);
            receiverSession.Start(receiverPort);

            if (recovery)
            {
                // parity with the real client: the facade wires KeyframeRequested to ForceKeyframe
                senderSession.KeyframeRequested += encoder.ForceKeyframe;
            }

            sampleRecovery = () => (receiverSession.NackCount, receiverSession.KeyframeRequestCount);

            double sendElapsed = await SendLoopAsync(provider, encoder, config, frame =>
            {
                accumulator.SendTicks[frame.Sequence] = frame.ProduceTick;
                accumulator.TotalBytes += frame.Encoded.Length;
                accumulator.Frames.Add((frame.Sequence, frame.Encoded.Length, frame.FrameType.ToString(), frame.EncodeMs));
                senderSession.SendFrame(frame.Encoded, frame.FrameType, config.Codec);
                return Task.CompletedTask;
            }, sink, accumulator, sampleRecovery, progress, cancellationToken);

            await Task.Delay(2000, CancellationToken.None);

            elapsed = sendElapsed;
            nackCount = receiverSession.NackCount;
            pliCount = receiverSession.KeyframeRequestCount;
            retransmitted = senderSession.RetransmittedFrames;
            droppedDatagrams = lossySenderTransport.DroppedCount;
            repairRttMs = receiverSession.SmoothedRepairRttMs;
        }

        BenchmarkSummary summary = BuildSummary(elapsed, accumulator, sink, nackCount, pliCount, retransmitted, droppedDatagrams, repairRttMs);
        List<FrameRecord> frames = BuildFrameRecords(accumulator, sink);

        progress?.Report($"Done: {summary.DeliveredFrames}/{summary.SentFrames} delivered ({summary.DeliveredPercent:F1}%) · " +
                         $"avg latency {summary.LatencyAvgMs:F1} ms · nack {nackCount} · pli {pliCount} · retrans {retransmitted}");

        return new BenchmarkResult(config, provider.Width, provider.Height, provider.Fps, summary, accumulator.Timeseries, frames);
    }

    private static async Task<double> SendLoopAsync(
        FileFrameProvider provider,
        IVideoEncoder encoder,
        BenchmarkConfig config,
        Func<PreparedFrame, Task> send,
        RecordingSink sink,
        Accumulator accumulator,
        Func<(int Nack, int Pli)>? sampleRecovery,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        long period = Stopwatch.Frequency / provider.Fps;
        long next = Stopwatch.GetTimestamp();
        long runStart = next;
        uint sequence = 0;
        int lastSecond = -1;
        int lastSent = 0;
        int lastDelivered = 0;
        long lastBytes = 0;
        long lastDeliveredBytes = 0;
        int lastNack = 0;
        int lastPli = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            double elapsedSeconds = (double)(Stopwatch.GetTimestamp() - runStart) / Stopwatch.Frequency;

            if (elapsedSeconds >= config.DurationSeconds)
            {
                break;
            }

            byte[] bgr = provider.ReadFrame();
            var frame = new VideoFrame(bgr, provider.Width, provider.Height);

            long encodeStart = Stopwatch.GetTimestamp();
            (byte[] encoded, FrameType frameType) = encoder.Encode(frame);
            double encodeMs = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;

            if (encoded.Length > 0)
            {
                sequence++;
                var prepared = new PreparedFrame(sequence, encoded, frameType, encodeMs, Stopwatch.GetTimestamp());
                await send(prepared);
            }

            int sampleSecond = (int)((double)(Stopwatch.GetTimestamp() - runStart) / Stopwatch.Frequency);

            if (sampleSecond > lastSecond)
            {
                (int nackDelta, int pliDelta) = sampleRecovery?.Invoke() ?? (0, 0);

                int delivered = sink.DeliveredCount;
                long deliveredBytes = sink.DeliveredBytes;

                accumulator.Timeseries.Add(new TimeseriesPoint(
                    sampleSecond,
                    (int)sequence - lastSent,
                    delivered - lastDelivered,
                    (accumulator.TotalBytes - lastBytes) / 1024.0,
                    (deliveredBytes - lastDeliveredBytes) / 1024.0,
                    nackDelta - lastNack,
                    pliDelta - lastPli));

                lastSent = (int)sequence;
                lastDelivered = delivered;
                lastBytes = accumulator.TotalBytes;
                lastDeliveredBytes = deliveredBytes;
                lastNack = nackDelta;
                lastPli = pliDelta;
                lastSecond = sampleSecond;

                if (sampleSecond % 5 == 0)
                {
                    progress?.Report($"  {sampleSecond,3}s: sent {sequence}, delivered {delivered}");
                }
            }

            next += period;
            long sleepTicks = next - Stopwatch.GetTimestamp();

            if (sleepTicks > 0)
            {
                await Task.Delay(TimeSpan.FromTicks(sleepTicks), cancellationToken);
            }
            else
            {
                next = Stopwatch.GetTimestamp();
            }
        }

        return (double)(Stopwatch.GetTimestamp() - runStart) / Stopwatch.Frequency;
    }

    private static List<FrameRecord> BuildFrameRecords(Accumulator accumulator, RecordingSink sink)
    {
        var records = new List<FrameRecord>(accumulator.Frames.Count);

        foreach ((uint sequence, long bytes, string frameType, double encodeMs) in accumulator.Frames)
        {
            double? latency = null;

            if (accumulator.SendTicks.TryGetValue(sequence, out long sendTick) &&
                sink.GetDelivery(sequence) is { } delivery)
            {
                latency = Stopwatch.GetElapsedTime(sendTick, delivery.Tick).TotalMilliseconds;
            }

            records.Add(new FrameRecord(sequence, bytes, frameType, encodeMs, latency));
        }

        return records;
    }

    private static BenchmarkSummary BuildSummary(
        double elapsed,
        Accumulator accumulator,
        RecordingSink sink,
        int nackCount,
        int pliCount,
        int retransmitted,
        int droppedDatagrams,
        double repairRttMs)
    {
        int sent = accumulator.Frames.Count;
        int delivered = sink.DeliveredCount;

        var latencies = new List<double>();

        foreach (uint sequence in accumulator.SendTicks.Keys)
        {
            if (sink.GetDelivery(sequence) is { } delivery)
            {
                latencies.Add(Stopwatch.GetElapsedTime(accumulator.SendTicks[sequence], delivery.Tick).TotalMilliseconds);
            }
        }

        latencies.Sort();

        List<long> deliveredTicks = sink.DeliveredTicksSorted();
        double maxGapMs = 0;

        for (int i = 1; i < deliveredTicks.Count; i++)
        {
            maxGapMs = Math.Max(maxGapMs, Stopwatch.GetElapsedTime(deliveredTicks[i - 1], deliveredTicks[i]).TotalMilliseconds);
        }

        double encodeAvg = sent == 0 ? 0 : accumulator.Frames.Average(f => f.EncodeMs);
        double encodeMax = sent == 0 ? 0 : accumulator.Frames.Max(f => f.EncodeMs);

        return new BenchmarkSummary(
            ElapsedSeconds: elapsed,
            SentFrames: sent,
            DeliveredFrames: delivered,
            DeliveredPercent: sent == 0 ? 0 : 100.0 * delivered / sent,
            DecodableFrames: sink.DecodableCount,
            DecodablePercent: sent == 0 ? 0 : 100.0 * sink.DecodableCount / sent,
            DroppedDatagrams: droppedDatagrams,
            FpsSent: elapsed > 0 ? sent / elapsed : 0,
            FpsDelivered: elapsed > 0 ? delivered / elapsed : 0,
            KbSentTotal: accumulator.TotalBytes / 1024.0,
            KbDeliveredTotal: sink.DeliveredBytes / 1024.0,
            GoodputKbPerSec: elapsed > 0 ? sink.DeliveredBytes / 1024.0 / elapsed : 0,
            AvgFrameKb: sent == 0 ? 0 : accumulator.TotalBytes / 1024.0 / sent,
            NackCount: nackCount,
            PliCount: pliCount,
            RetransmittedFrames: retransmitted,
            LatencyAvgMs: latencies.Count == 0 ? 0 : latencies.Average(),
            LatencyP50Ms: Percentile(latencies, 0.50),
            LatencyP95Ms: Percentile(latencies, 0.95),
            LatencyP99Ms: Percentile(latencies, 0.99),
            LatencyMaxMs: latencies.Count == 0 ? 0 : latencies[^1],
            EncodeAvgMs: encodeAvg,
            EncodeMaxMs: encodeMax,
            OutOfOrderCount: sink.OutOfOrderCount,
            MaxGapMs: maxGapMs,
            RepairRttMs: repairRttMs);
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        int index = (int)Math.Clamp(Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}
