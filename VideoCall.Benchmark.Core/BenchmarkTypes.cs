using VideoCall.Protocol.Enums;

namespace VideoCall.Benchmark.Core;

public enum TransportKind
{
    CustomUdp = 0,
    RawUdp = 1,
    Tcp = 2,
}

public static class TransportKindExtensions
{
    public static string DisplayName(this TransportKind kind) => kind switch
    {
        TransportKind.CustomUdp => "custom-udp",
        TransportKind.RawUdp => "raw-udp",
        TransportKind.Tcp => "tcp",
        _ => kind.ToString(),
    };
}

public sealed record BenchmarkConfig(
    string TestName,
    string VideoFilePath,
    int DurationSeconds,
    TransportKind Transport,
    VideoCodec Codec,
    int LossPercent,
    int Seed = 11);

public sealed record FrameRecord(uint Sequence, long SizeBytes, string FrameType, double EncodeMs, double? LatencyMs);

public sealed record TimeseriesPoint(
    int Second, int Sent, int Delivered, double KbSent, double KbDelivered, int Nack, int Pli);

public sealed record BenchmarkSummary(
    double ElapsedSeconds,
    int SentFrames,
    int DeliveredFrames,
    double DeliveredPercent,
    int DecodableFrames,
    double DecodablePercent,
    int DroppedDatagrams,
    double FpsSent,
    double FpsDelivered,
    double KbSentTotal,
    double KbDeliveredTotal,
    double GoodputKbPerSec,
    double AvgFrameKb,
    int NackCount,
    int PliCount,
    int RetransmittedFrames,
    double LatencyAvgMs,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyP99Ms,
    double LatencyMaxMs,
    double EncodeAvgMs,
    double EncodeMaxMs,
    int OutOfOrderCount,
    double MaxGapMs);

public sealed record BenchmarkResult(
    BenchmarkConfig Config,
    int Width,
    int Height,
    int Fps,
    BenchmarkSummary Summary,
    IReadOnlyList<TimeseriesPoint> Timeseries,
    IReadOnlyList<FrameRecord> Frames);
