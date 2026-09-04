using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VideoCall.Benchmark.Core;
using VideoCall.Protocol.Enums;

namespace VideoCall.Benchmark.Wpf;

public partial class MainWindow : Window
{
    private readonly IProgress<string> _progress;
    private CancellationTokenSource? _cts;
    private string _lastResultsFolder = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _progress = new Progress<string>(message =>
        {
            LogText.Text += message + Environment.NewLine;
            LogScroll.ScrollToEnd();
        });
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select video file",
            Filter = "Video files|*.mp4;*.avi;*.mkv;*.mov;*.wmv|All files|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            VideoFileBox.Text = dialog.FileName;

            if (TestNameBox.Text.Length == 0 || TestNameBox.Text.StartsWith("test", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = Path.GetFileNameWithoutExtension(dialog.FileName);
                TestNameBox.Text = $"{(CodecCombo.SelectedIndex == 1 ? "jpeg" : "h264")}-{TransportName()}-{baseName}";
            }
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadConfig(out BenchmarkConfig? config))
        {
            return;
        }

        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        OpenFolderButton.IsEnabled = false;
        SummaryList.Items.Clear();
        LogText.Text = string.Empty;
        _cts = new CancellationTokenSource();

        try
        {
            string folder;

            if (SweepCheck.IsChecked == true)
            {
                int[] levels = Enumerable.Range(0, 11).ToArray();
                _progress.Report($"Sweep: {levels.Length} runs over loss {levels[0]}–{levels[^1]}% (each ~{config.DurationSeconds + 3}s)");
                folder = await ResultWriter.WriteSweepAsync(config, levels, _progress, _cts.Token);
            }
            else
            {
                BenchmarkResult result = await BenchmarkRunner.RunAsync(config, _progress, _cts.Token);
                folder = ResultWriter.Write(result);
                ShowSummary(result);
            }

            _lastResultsFolder = Path.GetFullPath(folder);
            OpenFolderButton.IsEnabled = true;
            SetStatus($"Finished. Results in {_lastResultsFolder}", false);
            _progress.Report($"Results written to {_lastResultsFolder}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
            _progress.Report("ERROR: " + ex.Message);
        }
        finally
        {
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _progress.Report("Stopping…");
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResultsFolder.Length > 0 && Directory.Exists(_lastResultsFolder))
        {
            Process.Start(new ProcessStartInfo { FileName = _lastResultsFolder, UseShellExecute = true });
        }
    }

    private bool TryReadConfig(out BenchmarkConfig? config)
    {
        config = null;

        string name = TestNameBox.Text.Trim();

        if (name.Length == 0)
        {
            SetStatus("Enter a test name.", true);
            return false;
        }

        string file = VideoFileBox.Text.Trim();

        if (file.Length == 0 || !File.Exists(file))
        {
            SetStatus("Pick an existing video file.", true);
            return false;
        }

        if (!int.TryParse(DurationBox.Text.Trim(), out int duration) || duration is < 5 or > 600)
        {
            SetStatus("Duration must be 5–600 seconds.", true);
            return false;
        }

        if (!int.TryParse(LossBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int loss) || loss is < 0 or > 100)
        {
            SetStatus("Loss must be 0–100 %.", true);
            return false;
        }

        if (!int.TryParse(PingBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) || delay is < 0 or > 2000)
        {
            SetStatus("Ping must be 0–2000 ms.", true);
            return false;
        }

        var transport = TransportCombo.SelectedIndex switch
        {
            1 => TransportKind.RawUdp,
            2 => TransportKind.Tcp,
            _ => TransportKind.CustomUdp,
        };

        VideoCodec codec = CodecCombo.SelectedIndex == 1 ? VideoCodec.Jpeg : VideoCodec.H264;

        config = new BenchmarkConfig(name, file, duration, transport, codec, loss, delay);
        SetStatus("Running…", false);
        return true;
    }

    private string TransportName() => TransportCombo.SelectedIndex switch
    {
        1 => "raw-udp",
        2 => "tcp",
        _ => "custom-udp",
    };

    private void ShowSummary(BenchmarkResult result)
    {
        BenchmarkSummary s = result.Summary;

        var rows = new (string Metric, string Value)[]
        {
            ("transport", result.Config.Transport.DisplayName()),
            ("codec", result.Config.Codec.ToString()),
            ("loss %", result.Config.LossPercent.ToString()),
            ("one-way delay (ping)", $"{result.Config.DelayMs} ms"),
            ("elapsed", $"{s.ElapsedSeconds:F1} s"),
            ("sent frames", s.SentFrames.ToString()),
            ("delivered frames", s.DeliveredFrames.ToString()),
            ("delivered %", $"{s.DeliveredPercent:F1} %"),
            ("decodable (renderable) %", $"{s.DecodablePercent:F1} %"),
            ("fps sent / delivered", $"{s.FpsSent:F1} / {s.FpsDelivered:F1}"),
            ("goodput", $"{s.GoodputKbPerSec:F1} KB/s"),
            ("avg frame size", $"{s.AvgFrameKb:F1} KB"),
            ("nack (delta retransmit req)", s.NackCount.ToString()),
            ("pli (keyframe requests)", s.PliCount.ToString()),
            ("retransmitted frames", s.RetransmittedFrames.ToString()),
            ("dropped datagrams (simulated)", s.DroppedDatagrams.ToString()),
            ("latency avg / p50", $"{s.LatencyAvgMs:F1} / {s.LatencyP50Ms:F1} ms"),
            ("latency p95 / p99 / max", $"{s.LatencyP95Ms:F1} / {s.LatencyP99Ms:F1} / {s.LatencyMaxMs:F1} ms"),
            ("longest delivery gap (freeze)", $"{s.MaxGapMs:F0} ms"),
            ("measured repair RTT", $"{s.RepairRttMs:F1} ms"),
            ("encode avg / max", $"{s.EncodeAvgMs:F1} / {s.EncodeMaxMs:F1} ms"),
            ("out-of-order deliveries", s.OutOfOrderCount.ToString()),
        };

        foreach ((string metric, string value) in rows)
        {
            SummaryList.Items.Add(new MetricRow(metric, value));
        }
    }

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = error
            ? (System.Windows.Media.Brush)FindResource("Brush.Danger")
            : (System.Windows.Media.Brush)FindResource("Brush.TextSecondary");
    }

    private sealed record MetricRow(string Metric, string Value);
}
