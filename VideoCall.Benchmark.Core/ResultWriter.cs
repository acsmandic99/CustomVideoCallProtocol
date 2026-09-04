using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VideoCall.Benchmark.Core;

/// <summary>
/// Writes one benchmark run (or a loss sweep) into its own timestamped folder:
/// meta.json, summary.csv, timeseries.csv, frames.csv (+ sweep-summary.csv for sweeps).
/// </summary>
public static class ResultWriter
{
    private const string DefaultRootDirectory = "BenchmarkResults";

    public static string Write(BenchmarkResult result, string rootDirectory = DefaultRootDirectory)
    {
        string directory = CreateRunDirectory(rootDirectory, result.Config.TestName, null);
        WriteRun(result, directory);
        return directory;
    }

    public static async Task<string> WriteSweepAsync(
        BenchmarkConfig baseConfig,
        IReadOnlyList<int> lossLevels,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string rootDirectory = DefaultRootDirectory)
    {
        string directory = CreateRunDirectory(rootDirectory, baseConfig.TestName, "sweep");

        var summaries = new List<(int Loss, BenchmarkResult Result)>();

        foreach (int loss in lossLevels)
        {
            progress?.Report($"=== loss {loss}% ===");
            BenchmarkConfig config = baseConfig with { TestName = $"{baseConfig.TestName}-loss{loss}", LossPercent = loss };
            BenchmarkResult result = await BenchmarkRunner.RunAsync(config, progress, cancellationToken);
            WriteRun(result, Path.Combine(directory, $"loss-{loss:D2}percent"));
            summaries.Add((loss, result));
        }

        await using (var sweep = new StreamWriter(Path.Combine(directory, "sweep-summary.csv"), false, new UTF8Encoding(true)))
        {
            await sweep.WriteLineAsync("loss,sent,delivered,deliveredPercent,decodablePercent,fpsSent,fpsDelivered,goodputKbPerSec," +
                                       "nack,pli,retransmitted,latencyAvgMs,latencyP50Ms,latencyP95Ms,latencyP99Ms,latencyMaxMs,maxGapMs,outOfOrder,avgFrameKb,repairRttMs");

            foreach ((int loss, BenchmarkResult r) in summaries)
            {
                BenchmarkSummary s = r.Summary;
                await sweep.WriteLineAsync(string.Join(',',
                    loss, s.SentFrames, s.DeliveredFrames, s.DeliveredPercent.ToString("F2", CultureInfo.InvariantCulture),
                    s.DecodablePercent.ToString("F2", CultureInfo.InvariantCulture),
                    s.FpsSent.ToString("F2", CultureInfo.InvariantCulture), s.FpsDelivered.ToString("F2", CultureInfo.InvariantCulture),
                    s.GoodputKbPerSec.ToString("F2", CultureInfo.InvariantCulture), s.NackCount, s.PliCount, s.RetransmittedFrames,
                    s.LatencyAvgMs.ToString("F2", CultureInfo.InvariantCulture), s.LatencyP50Ms.ToString("F2", CultureInfo.InvariantCulture),
                    s.LatencyP95Ms.ToString("F2", CultureInfo.InvariantCulture), s.LatencyP99Ms.ToString("F2", CultureInfo.InvariantCulture),
                    s.LatencyMaxMs.ToString("F2", CultureInfo.InvariantCulture), s.MaxGapMs.ToString("F2", CultureInfo.InvariantCulture),
                    s.OutOfOrderCount, s.AvgFrameKb.ToString("F2", CultureInfo.InvariantCulture),
                    s.RepairRttMs.ToString("F2", CultureInfo.InvariantCulture)));
            }
        }

        return directory;
    }

    private static string CreateRunDirectory(string root, string testName, string? suffix)
    {
        string safeName = string.Join("_", testName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        if (safeName.Length == 0)
        {
            safeName = "test";
        }

        string directory = Path.Combine(root, $"{safeName}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");

        if (suffix is not null)
        {
            directory += "-" + suffix;
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteRun(BenchmarkResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        WriteMeta(result, directory);
        WriteSummary(result.Summary, directory);
        WriteTimeseries(result.Timeseries, directory);
        WriteFrames(result.Frames, directory);
    }

    private static void WriteMeta(BenchmarkResult result, string directory)
    {
        var meta = new Dictionary<string, object>
        {
            ["testName"] = result.Config.TestName,
            ["videoFile"] = Path.GetFullPath(result.Config.VideoFilePath),
            ["durationSeconds"] = result.Config.DurationSeconds,
            ["transport"] = result.Config.Transport.DisplayName(),
            ["codec"] = result.Config.Codec.ToString(),
            ["lossPercent"] = result.Config.LossPercent,
            ["delayMs"] = result.Config.DelayMs,
            ["lossSeed"] = result.Config.Seed,
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["sourceFps"] = result.Fps,
            ["startedUtc"] = DateTime.UtcNow,
        };

        File.WriteAllText(
            Path.Combine(directory, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSummary(BenchmarkSummary s, string directory)
    {
        var rows = new (string Key, string Value)[]
        {
            ("elapsedSeconds", F(s.ElapsedSeconds)),
            ("sentFrames", s.SentFrames.ToString()),
            ("deliveredFrames", s.DeliveredFrames.ToString()),
            ("deliveredPercent", F(s.DeliveredPercent)),
            ("decodableFrames", s.DecodableFrames.ToString()),
            ("decodablePercent", F(s.DecodablePercent)),
            ("fpsSent", F(s.FpsSent)),
            ("fpsDelivered", F(s.FpsDelivered)),
            ("kbSentTotal", F(s.KbSentTotal)),
            ("kbDeliveredTotal", F(s.KbDeliveredTotal)),
            ("goodputKbPerSec", F(s.GoodputKbPerSec)),
            ("avgFrameKb", F(s.AvgFrameKb)),
            ("droppedDatagrams", s.DroppedDatagrams.ToString()),
            ("nackCount", s.NackCount.ToString()),
            ("pliCount", s.PliCount.ToString()),
            ("retransmittedFrames", s.RetransmittedFrames.ToString()),
            ("latencyAvgMs", F(s.LatencyAvgMs)),
            ("latencyP50Ms", F(s.LatencyP50Ms)),
            ("latencyP95Ms", F(s.LatencyP95Ms)),
            ("latencyP99Ms", F(s.LatencyP99Ms)),
            ("latencyMaxMs", F(s.LatencyMaxMs)),
            ("encodeAvgMs", F(s.EncodeAvgMs)),
            ("encodeMaxMs", F(s.EncodeMaxMs)),
            ("outOfOrderCount", s.OutOfOrderCount.ToString()),
            ("maxGapMs", F(s.MaxGapMs)),
            ("repairRttMeasuredMs", F(s.RepairRttMs)),
        };

        var builder = new StringBuilder("metric,value\n");

        foreach ((string key, string value) in rows)
        {
            builder.Append(key).Append(',').AppendLine(value);
        }

        File.WriteAllText(Path.Combine(directory, "summary.csv"), builder.ToString());
    }

    private static void WriteTimeseries(IReadOnlyList<TimeseriesPoint> points, string directory)
    {
        var builder = new StringBuilder("second,sent,delivered,kbSent,kbDelivered,nack,pli\n");

        foreach (TimeseriesPoint p in points)
        {
            builder.Append(p.Second).Append(',')
                .Append(p.Sent).Append(',')
                .Append(p.Delivered).Append(',')
                .Append(F(p.KbSent)).Append(',')
                .Append(F(p.KbDelivered)).Append(',')
                .Append(p.Nack).Append(',')
                .Append(p.Pli).AppendLine();
        }

        File.WriteAllText(Path.Combine(directory, "timeseries.csv"), builder.ToString());
    }

    private static void WriteFrames(IReadOnlyList<FrameRecord> frames, string directory)
    {
        var builder = new StringBuilder("sequence,frameType,bytes,encodeMs,latencyMs\n");

        foreach (FrameRecord f in frames)
        {
            builder.Append(f.Sequence).Append(',')
                .Append(f.FrameType).Append(',')
                .Append(f.SizeBytes).Append(',')
                .Append(F(f.EncodeMs)).Append(',')
                .AppendLine(f.LatencyMs is null ? "" : F(f.LatencyMs.Value));
        }

        File.WriteAllText(Path.Combine(directory, "frames.csv"), builder.ToString());
    }

    private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
