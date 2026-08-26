using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoCall.Codecs;
using VideoCall.Codecs.FFmpeg;
using VideoCall.Protocol.Enums;

namespace VideoCall.Client.Wpf;

public partial class MainWindow : Window
{
    private const int ServerPort = 5000;

    private readonly DispatcherTimer _statusTimer;
    private VideoCallClient _client = new();

    private WriteableBitmap? _localBitmap;
    private WriteableBitmap? _remoteBitmap;

    private string _videoFilePath = string.Empty;
    private int _selectedDropPercent;
    private int _sentLastTick;
    private int _receivedLastTick;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        Closed += (_, _) => _client.Dispose();
        string[] micNames = Array.Empty<string>();

        try
        {
            micNames = Codecs.Audio.AudioCapture.GetDeviceNames();
        }
        catch
        {
        }

        foreach (string name in micNames)
        {
            MicCombo.Items.Add(name);
        }

        if (MicCombo.Items.Count > 0)
        {
            MicCombo.SelectedIndex = 0;
        }

        WireClient(_client);
    }

    private void WireClient(VideoCallClient client)
    {
        client.IncomingCall += m => Dispatcher.BeginInvoke(() =>
        {
            AcceptButton.IsEnabled = true;
            RejectButton.IsEnabled = true;
            StatusText.Text = $"Incoming call from '{m.CallerId}'.";
        });

        client.Ringing += () => Dispatcher.BeginInvoke(() => StatusText.Text = "Ringing...");

        client.CallEstablished += () => Dispatcher.BeginInvoke(() =>
        {
            CallButton.IsEnabled = false;
            AcceptButton.IsEnabled = false;
            RejectButton.IsEnabled = false;
            HangupButton.IsEnabled = true;
            _sentLastTick = 0;
            _receivedLastTick = 0;
            _statusTimer.Start();
            StatusText.Text = $"Call established. Media port {client.MediaPort}, sending to {client.RemoteEndpoint}.";
        });

        client.CallRejected += reason => Dispatcher.BeginInvoke(() =>
        {
            CallButton.IsEnabled = true;
            StatusText.Text = $"Call rejected: {reason}";
        });

        client.CallEnded += reason => Dispatcher.BeginInvoke(() =>
        {
            _statusTimer.Stop();
            CallButton.IsEnabled = true;
            AcceptButton.IsEnabled = false;
            RejectButton.IsEnabled = false;
            HangupButton.IsEnabled = false;
            StatusText.Text = reason;
        });

        client.DisconnectedFromServer += () => Dispatcher.BeginInvoke(() =>
        {
            _statusTimer.Stop();
            CallButton.IsEnabled = false;
            AcceptButton.IsEnabled = false;
            RejectButton.IsEnabled = false;
            HangupButton.IsEnabled = false;
            RegisterButton.IsEnabled = true;
            StatusText.Text = "Disconnected from server.";
        });

        client.LocalVideoFrame += f => Dispatcher.BeginInvoke(() => WriteBitmap(ref _localBitmap, LocalImage, f));
        client.RemoteVideoFrame += f => Dispatcher.BeginInvoke(() => WriteBitmap(ref _remoteBitmap, RemoteImage, f));
        client.CameraError += r => Dispatcher.BeginInvoke(() => StatusText.Text = $"Camera error: {r}");
        client.SendError += ex => Dispatcher.BeginInvoke(() => StatusText.Text = $"Send error: {ex.Message}");
        client.DecodeError += ex => Dispatcher.BeginInvoke(() => StatusText.Text = $"Decode error: {ex.Message}");
    }

    private VideoCallClient.SourceKind CurrentSource()
    {
        return SourceCombo.SelectedIndex switch
        {
            1 => VideoCallClient.SourceKind.Synthetic,
            2 => VideoCallClient.SourceKind.VideoFile,
            _ => VideoCallClient.SourceKind.WebCamera,
        };
    }

    private void ApplyConfiguration()
    {
        VideoCodec codec = CodecCombo.SelectedIndex == 1 ? VideoCodec.Jpeg : VideoCodec.H264;
        _client.MicrophoneIndex = MicCombo.SelectedIndex < 0 ? 0 : MicCombo.SelectedIndex;
        _client.Configure(CurrentSource(), _videoFilePath, codec, _selectedDropPercent);
    }

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            StatusText.Text = "Enter a name first.";
            return;
        }

        RegisterButton.IsEnabled = false;
        StatusText.Text = "Connecting...";

        try
        {
            ApplyConfiguration();
            await _client.ConnectAndRegisterAsync(ServerBox.Text.Trim(), ServerPort, name);
            CallButton.IsEnabled = true;
            StatusText.Text = $"Registered as '{name}'. Media port {_client.MediaPort}. Enter a name and press Call.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.StartsWith("Registration") ? ex.Message : $"Connection failed: {ex.Message}";
            RegisterButton.IsEnabled = true;
        }
    }

    private async void CallButton_Click(object sender, RoutedEventArgs e)
    {
        string callee = CalleeBox.Text.Trim();

        if (callee.Length == 0 || !_client.IsRegistered)
        {
            return;
        }

        CallButton.IsEnabled = false;
        StatusText.Text = $"Calling '{callee}'...";

        try
        {
            ApplyConfiguration();
            await _client.CallAsync(callee);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Call failed: {ex.Message}";
            CallButton.IsEnabled = true;
        }
    }

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
        ApplyConfiguration();
        await _client.AcceptCallAsync();
    }

    private async void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
        await _client.RejectCallAsync();
        StatusText.Text = "Call rejected.";
    }

    private async void HangupButton_Click(object sender, RoutedEventArgs e)
    {
        await _client.HangupAsync();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo.SelectedIndex != 2)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select video file",
            Filter = "Video files|*.mp4;*.avi;*.mkv;*.mov;*.wmv|All files|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            _videoFilePath = dialog.FileName;
            StatusText.Text = $"Video file selected: {_videoFilePath}";
        }
        else
        {
            SourceCombo.SelectedIndex = 0;
            StatusText.Text = "No video file selected, back to web camera.";
        }
    }

    private void MicToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _client.MicrophoneMuted = MicToggle.IsChecked != true;
        StatusText.Text = MicToggle.IsChecked == true ? "Microphone on." : "Microphone muted.";
    }

    private void CamToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _client.CameraMuted = CamToggle.IsChecked != true;
        StatusText.Text = CamToggle.IsChecked == true ? "Camera on." : "Camera off.";
    }

    private void LossButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        string text = button.Content.ToString() ?? "0%";
        _selectedDropPercent = int.Parse(text.TrimEnd('%'));
        _client.DropPercent = _selectedDropPercent;

        foreach (Button b in LossPanel.Children.OfType<Button>())
        {
            b.FontWeight = b == button ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private void WriteBitmap(ref WriteableBitmap? bitmap, Image image, VideoFrame frame)
    {
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            image.Source = bitmap;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgr24Data, frame.Width * 3, 0);
    }

    private void UpdateStatus()
    {
        int sentFps = (_client.SentFrames - _sentLastTick) * 2;
        int receivedFps = (_client.ReceivedFrames - _receivedLastTick) * 2;
        _sentLastTick = _client.SentFrames;
        _receivedLastTick = _client.ReceivedFrames;

        StatusText.Text = $"In call. sent {_client.SentFrames} ({sentFps} fps), received {_client.ReceivedFrames} ({receivedFps} fps), " +
                          $"dgrams-in {_client.ReceivedDatagrams}, raw {_client.RawReceived}, enc0 {_client.EmptyEncodes}, " +
                          $"nack {_client.NackCount}, pli {_client.KeyframeRequestCount}, on {_client.MediaPort}, " +
                          $"to {_client.RemoteEndpoint}, loss {_client.DropPercent}%";
    }
}
