using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoCall.Codecs;
using VideoCall.Protocol.Enums;

namespace VideoCall.Client.Wpf.Showcase;

public partial class MainWindow : Window
{
    private const int DefaultServerPort = 5000;

    private readonly DispatcherTimer _statsTimer;
    private readonly VideoCallClient _client = new();

    private WriteableBitmap? _localBitmap;
    private WriteableBitmap? _remoteBitmap;
    private bool _pipShowsLocal = true;
    private bool _statsVisible;
    private int _sentLastTick;
    private int _receivedLastTick;
    private string _calleeName = string.Empty;
    private string _remoteName = string.Empty;
    private string _serverHost = "127.0.0.1";

    public MainWindow()
    {
        InitializeComponent();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        Closed += (_, _) => _client.Dispose();

        WireClient();
    }

    private void WireClient()
    {
        _client.IncomingCall += m => Dispatcher.BeginInvoke(() =>
        {
            _remoteName = m.CallerId;
            SystemSounds.Asterisk.Play();
            IncomingFrom.Text = $"{m.CallerId} is calling you";
            IncomingOverlay.Visibility = Visibility.Visible;
            CallButton.IsEnabled = false;
            SetStatus($"Incoming call from '{m.CallerId}'.", false);
        });

        _client.Ringing += () => Dispatcher.BeginInvoke(() =>
        {
            RingingTitle.Text = $"Calling {_calleeName}…";
            RingingOverlay.Visibility = Visibility.Visible;
        });

        _client.CallEstablished += () => Dispatcher.BeginInvoke(ShowInCall);

        _client.CallRejected += reason => Dispatcher.BeginInvoke(() =>
        {
            ResetStage();
            SetStatus($"Call rejected: {reason}", false);
        });

        _client.CallEnded += reason => Dispatcher.BeginInvoke(() =>
        {
            ResetStage();
            SetStatus(reason, false);
        });

        _client.DisconnectedFromServer += () => Dispatcher.BeginInvoke(() =>
        {
            ResetStage();
            RegisterView.Visibility = Visibility.Visible;
            HomeView.Visibility = Visibility.Collapsed;
            RegisterButton.IsEnabled = true;
            SetRegisterStatus("Disconnected from server.", true);
        });

        _client.LocalVideoFrame += f => Dispatcher.BeginInvoke(() => WriteFrame(ref _localBitmap, f));
        _client.RemoteVideoFrame += f => Dispatcher.BeginInvoke(() => WriteFrame(ref _remoteBitmap, f));
        _client.CameraError += r => Dispatcher.BeginInvoke(() => SetStatus($"Camera error: {r}", true));
        _client.SendError += ex => Dispatcher.BeginInvoke(() => SetStatus($"Send error: {ex.Message}", true));
        _client.DecodeError += ex => Dispatcher.BeginInvoke(() => SetStatus($"Decode error: {ex.Message}", true));
    }

    // ---------- state transitions ----------

    private void ShowInCall()
    {
        RingingOverlay.Visibility = Visibility.Collapsed;
        IncomingOverlay.Visibility = Visibility.Collapsed;
        IdlePlaceholder.Visibility = Visibility.Collapsed;
        StageBorder.Background = (Brush)FindResource("Brush.StageActive");
        PipBorder.Visibility = Visibility.Visible;
        CallBar.Visibility = Visibility.Visible;
        StageLabel.Visibility = Visibility.Visible;
        CallButton.IsEnabled = false;
        _sentLastTick = 0;
        _receivedLastTick = 0;
        _statsTimer.Start();
        AssignSources();
        SetStatus(_remoteName.Length > 0
            ? $"In call with '{_remoteName}'. Media port {_client.MediaPort}."
            : $"Call established. Media port {_client.MediaPort}.", false);
    }

    private void ResetStage()
    {
        RingingOverlay.Visibility = Visibility.Collapsed;
        IncomingOverlay.Visibility = Visibility.Collapsed;
        CallBar.Visibility = Visibility.Collapsed;
        PipBorder.Visibility = Visibility.Collapsed;
        StageLabel.Visibility = Visibility.Collapsed;
        StatsHud.Visibility = Visibility.Collapsed;
        _statsVisible = false;
        StatsIcon.Foreground = Brushes.White;
        IdlePlaceholder.Visibility = Visibility.Visible;
        StageBorder.Background = (Brush)FindResource("Brush.StageIdle");
        BigImage.Source = null;
        PipImage.Source = null;
        _localBitmap = null;
        _remoteBitmap = null;
        _remoteName = string.Empty;
        _statsTimer.Stop();
        MicToggle.IsChecked = false;
        CamToggle.IsChecked = false;
        CallButton.IsEnabled = true;
    }

    // ---------- registration ----------

    private async void RegisterButton_Click(object sender, RoutedEventArgs e) => await RegisterAsync();

    private async void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await RegisterAsync();
        }
    }

    private async Task RegisterAsync()
    {
        string name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            SetRegisterStatus("Enter a name first.", true);
            return;
        }

        (string host, int port) = ParseServer(ServerBox.Text);
        _serverHost = host;
        RegisterButton.IsEnabled = false;
        SetRegisterStatus("Connecting…", false);

        try
        {
            ApplyDefaults();
            await _client.ConnectAndRegisterAsync(host, port, name);

            IdentityName.Text = name;
            AvatarText.Text = Initials(name);
            Title = $"CustomVideoCall — {name}";
            RegisterView.Visibility = Visibility.Collapsed;
            HomeView.Visibility = Visibility.Visible;
            ResetStage();
            SetStatus($"Registered as '{name}'. Enter the other user's name and call.", false);
        }
        catch (RegistrationFailedException ex)
        {
            SetRegisterStatus(ex.Message, true);
            RegisterButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetRegisterStatus($"Connection failed: {ex.Message}", true);
            RegisterButton.IsEnabled = true;
        }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        await _client.DisconnectAsync();
        Title = "CustomVideoCall";
        RegisterView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        RegisterButton.IsEnabled = true;
        SetRegisterStatus("Signed out. Enter a name to register again.", false);
    }

    // ---------- calling ----------

    private async void CallButton_Click(object sender, RoutedEventArgs e) => await CallAsync();

    private async void CalleeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CallButton.IsEnabled)
        {
            await CallAsync();
        }
    }

    private async Task CallAsync()
    {
        string callee = CalleeBox.Text.Trim();

        if (callee.Length == 0)
        {
            SetStatus("Enter a name to call.", true);
            return;
        }

        _calleeName = callee;
        _remoteName = callee;
        CallButton.IsEnabled = false;
        SetStatus($"Calling '{callee}'…", false);

        try
        {
            ApplyDefaults();
            await _client.CallAsync(callee);
        }
        catch (Exception ex)
        {
            ResetStage();
            SetStatus($"Call failed: {ex.Message}", true);
        }
    }

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        IncomingOverlay.Visibility = Visibility.Collapsed;
        ApplyDefaults();
        await _client.AcceptCallAsync();
    }

    private async void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        await _client.RejectCallAsync();
        ResetStage();
        SetStatus("Call declined.", false);
    }

    private async void CancelCallButton_Click(object sender, RoutedEventArgs e) => await _client.HangupAsync();

    private async void HangupButton_Click(object sender, RoutedEventArgs e) => await _client.HangupAsync();

    // ---------- call bar ----------

    private void MicToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        bool muted = MicToggle.IsChecked == true;
        _client.MicrophoneMuted = muted;
        MicIcon.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        MicOffIcon.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CamToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        bool muted = CamToggle.IsChecked == true;
        _client.CameraMuted = muted;
        CamSlash.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        PipMutePill.Visibility = muted && PipBorder.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void StatsButton_Click(object sender, RoutedEventArgs e)
    {
        _statsVisible = !_statsVisible;
        StatsHud.Visibility = _statsVisible ? Visibility.Visible : Visibility.Collapsed;
        StatsIcon.Foreground = _statsVisible ? (Brush)FindResource("Brush.AccentTint") : Brushes.White;

        if (_statsVisible)
        {
            UpdateStats();
        }
    }

    private void Pip_Click(object sender, MouseButtonEventArgs e)
    {
        _pipShowsLocal = !_pipShowsLocal;
        AssignSources();
    }

    // ---------- video ----------

    private void WriteFrame(ref WriteableBitmap? bitmap, VideoFrame frame)
    {
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgr24Data, frame.Width * 3, 0);
        AssignSources();
    }

    private void AssignSources()
    {
        if (_pipShowsLocal)
        {
            PipImage.Source = _localBitmap;
            BigImage.Source = _remoteBitmap;
            PipLabelText.Text = "You";
            StageLabelText.Text = "Remote · via protocol";
        }
        else
        {
            PipImage.Source = _remoteBitmap;
            BigImage.Source = _localBitmap;
            PipLabelText.Text = "Remote";
            StageLabelText.Text = "You · local camera";
        }
    }

    // ---------- stats ----------

    private void UpdateStats()
    {
        if (!_statsVisible)
        {
            return;
        }

        int sentFps = (_client.SentFrames - _sentLastTick) * 2;
        int receivedFps = (_client.ReceivedFrames - _receivedLastTick) * 2;
        _sentLastTick = _client.SentFrames;
        _receivedLastTick = _client.ReceivedFrames;

        StatsText.Text =
            $"TX {sentFps,3} fps   RX {receivedFps,3} fps\n" +
            $"sent {_client.SentFrames}   recv {_client.ReceivedFrames}\n" +
            $"udp-in {_client.ReceivedDatagrams} datagrams\n" +
            $"nack {_client.NackCount}   pli {_client.KeyframeRequestCount}\n" +
            $":{_client.MediaPort} → {_client.RemoteEndpoint}";
    }

    // ---------- helpers ----------

    private void ApplyDefaults()
    {
        _client.Configure(VideoCallClient.SourceKind.WebCamera, string.Empty, VideoCodec.H264, 0);

        // same-machine demo: advertise loopback as the media address so that
        // traffic stays on 127.0.0.1, where network emulators can intercept it
        bool loopbackServer = _serverHost is "127.0.0.1" or "localhost" or "::1";
        _client.MediaIpOverride = loopbackServer ? "127.0.0.1" : null;
    }

    private static (string Host, int Port) ParseServer(string text)
    {
        text = text.Trim();

        if (text.Length == 0)
        {
            text = "127.0.0.1";
        }

        int port = DefaultServerPort;
        int colon = text.LastIndexOf(':');

        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int parsed) && parsed > 0 && parsed <= 65535)
        {
            text = text[..colon];
            port = parsed;
        }

        return (text, port);
    }

    private static string Initials(string name)
    {
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return "?";
        }

        return parts.Length == 1
            ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
            : $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
    }

    private void SetStatus(string message, bool error)
    {
        HomeStatus.Text = message;
        HomeStatus.Foreground = error ? (Brush)FindResource("Brush.Danger") : (Brush)FindResource("Brush.TextSecondary");
    }

    private void SetRegisterStatus(string message, bool error)
    {
        RegisterStatus.Text = message;
        RegisterStatus.Foreground = error ? (Brush)FindResource("Brush.Danger") : (Brush)FindResource("Brush.TextSecondary");
    }
}
