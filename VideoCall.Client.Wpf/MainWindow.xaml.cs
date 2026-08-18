using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using VideoCall.Codecs;
using VideoCall.Codecs.Audio;
using VideoCall.Codecs.FFmpeg;
using VideoCall.Codecs.OpenCv;
using VideoCall.Media;
using VideoCall.Media.Testing;
using VideoCall.Media.Transport;
using VideoCall.Network.Signaling;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Client.Wpf;

public partial class MainWindow : Window, ISignalingListener
{
    private const string ServerHost = "127.0.0.1";
    private const int ServerPort = 5000;

    private readonly IVideoDecoder _jpegDecoder = new JpegVideoDecoder();
    private readonly DispatcherTimer _statusTimer;

    private IVideoEncoder? _encoder;
    private IVideoDecoder? _h264Decoder;
    private VideoCodec _videoCodec = VideoCodec.H264;

    private SignalingClient? _signaling;
    private MediaSession? _mediaSession;
    private LossyTransportDecorator? _lossyTransport;
    private ICamera? _camera;
    private AudioCapture? _audioCapture;
    private AudioPlayer? _audioPlayer;
    private DecoderSink? _sink;

    private WriteableBitmap? _localBitmap;
    private WriteableBitmap? _remoteBitmap;

    private string _myName = string.Empty;
    private ushort _localUdpPort;
    private Guid _activeCallId;
    private Guid _incomingCallId;
    private IPEndPoint? _remoteMediaEndpoint;

    private int _framesSent;
    private int _framesReceived;
    private int _sentLastTick;
    private int _receivedLastTick;
    private int _selectedDropPercent;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateMediaStatus();
        Closed += (_, _) => Shutdown();
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

        var codec = new BinaryMessageCodec(new DefaultSignalingMessageFactory());
        _signaling = new SignalingClient(codec, this, NullLogger<SignalingClient>.Instance);
        _localUdpPort = (ushort)Random.Shared.Next(20000, 25000);

        try
        {
            await _signaling.ConnectAsync(ServerBox.Text.Trim(), ServerPort);
            bool ok = await _signaling.RegisterAsync(name);

            if (!ok)
            {
                StatusText.Text = "Registration failed: name already taken.";
                await _signaling.DisconnectAsync();
                _signaling = null;
                RegisterButton.IsEnabled = true;
                return;
            }

            _myName = name;
            CallButton.IsEnabled = true;
            StatusText.Text = $"Registered as '{name}'. UDP port {_localUdpPort}. Enter a name and press Call.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connection failed: {ex.Message}";
            _signaling = null;
            RegisterButton.IsEnabled = true;
        }
    }

    private async void CallButton_Click(object sender, RoutedEventArgs e)
    {
        string callee = CalleeBox.Text.Trim();

        if (callee.Length == 0 || _signaling is null)
        {
            return;
        }

        if (callee == _myName)
        {
            StatusText.Text = "You cannot call yourself.";
            return;
        }

        CallButton.IsEnabled = false;
        StatusText.Text = $"Calling '{callee}'...";

        try
        {
            _activeCallId = await _signaling.CallAsync(callee, _signaling.LocalIp ?? "127.0.0.1", _localUdpPort);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Call failed: {ex.Message}";
            CallButton.IsEnabled = true;
        }
    }

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_signaling is null || _incomingCallId == Guid.Empty)
        {
            return;
        }

        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;

        _activeCallId = _incomingCallId;
        _incomingCallId = Guid.Empty;

        await _signaling.AcceptCallAsync(_activeCallId, _signaling.LocalIp ?? "127.0.0.1", _localUdpPort);

        if (_remoteMediaEndpoint is not null)
        {
            StartMedia(_remoteMediaEndpoint);
        }
    }

    private async void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_signaling is null || _incomingCallId == Guid.Empty)
        {
            return;
        }

        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;

        await _signaling.RejectCallAsync(_incomingCallId, "busy");
        _incomingCallId = Guid.Empty;
        StatusText.Text = "Call rejected.";
    }

    private async void HangupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_signaling is null || _activeCallId == Guid.Empty)
        {
            return;
        }

        StopMedia();
        await _signaling.HangupAsync(_activeCallId);
        _activeCallId = Guid.Empty;

        HangupButton.IsEnabled = false;
        CallButton.IsEnabled = true;
        StatusText.Text = "Hung up.";
    }

    private void LossButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        string text = button.Content.ToString() ?? "0%";
        _selectedDropPercent = int.Parse(text.TrimEnd('%'));

        if (_lossyTransport is not null)
        {
            _lossyTransport.DropPercent = _selectedDropPercent;
        }

        foreach (Button b in LossPanel.Children.OfType<Button>())
        {
            b.FontWeight = b == button ? FontWeights.Bold : FontWeights.Normal;
        }

        StatusText.Text = $"Outgoing loss set to {text} (applies to this client's outgoing packets).";
    }

    private void StartMedia(IPEndPoint remote)
    {
        int sourceIndex = SourceCombo.SelectedIndex;
        int width = 640;
        int height = 480;
        int fps = 30;

        if (sourceIndex == 2)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select video file",
                Filter = "Video files|*.mp4;*.avi;*.mkv;*.mov;*.wmv|All files|*.*",
            };

            if (dialog.ShowDialog() != true)
            {
                StatusText.Text = "No video file selected.";
                return;
            }

            var probe = FileVideoSource.Probe(dialog.FileName);

            if (probe is null)
            {
                StatusText.Text = $"Cannot open video file: {dialog.FileName}";
                return;
            }

            (width, height, fps) = probe.Value;
            _camera = new FileVideoSource(dialog.FileName);
        }
        else
        {
            _camera = sourceIndex == 1 ? new SyntheticCamera() : new OpenCvCamera();
        }

        _videoCodec = CodecCombo.SelectedIndex == 1 ? VideoCodec.Jpeg : VideoCodec.H264;
        _encoder = _videoCodec == VideoCodec.H264
            ? new H264VideoEncoder(width, height, fps)
            : new JpegVideoEncoder();

        _lossyTransport = new LossyTransportDecorator(new UdpMediaTransport(), _selectedDropPercent);
        _sink = new DecoderSink(this);

        _mediaSession = new MediaSession(_lossyTransport, remote, _sink);
        _mediaSession.KeyframeRequested += OnKeyframeRequested;
        _mediaSession.SendError += OnSendError;
        _mediaSession.Start(_localUdpPort);
        StatusText.Text = $"media: bound {_localUdpPort}, sending to {remote}";

        _camera.FrameCaptured += OnCameraFrame;
        _audioCapture = new AudioCapture();
        _audioCapture.ChunkCaptured += OnAudioChunk;
        _audioCapture.Start();
        _audioPlayer = new AudioPlayer();
        _audioPlayer.Start();
        _camera.Failed += OnCameraFailed;
        _camera.Start(width, height, fps);

        _framesSent = 0;
        _framesReceived = 0;
        _sentLastTick = 0;
        _receivedLastTick = 0;
        _statusTimer.Start();
        HangupButton.IsEnabled = true;
    }

    private void StopMedia()
    {
        _statusTimer.Stop();

        if (_camera is not null)
        {
            _camera.FrameCaptured -= OnCameraFrame;
            _camera.Failed -= OnCameraFailed;
            _camera.Dispose();
            _camera = null;
        }

        if (_audioCapture is not null)
        {
            _audioCapture.ChunkCaptured -= OnAudioChunk;
            _audioCapture.Dispose();
            _audioCapture = null;
        }

        _audioPlayer?.Dispose();
        _audioPlayer = null;

        if (_mediaSession is not null)
        {
            _mediaSession.KeyframeRequested -= OnKeyframeRequested;
            _mediaSession.SendError -= OnSendError;
            _mediaSession.Dispose();
            _mediaSession = null;
        }

        _encoder?.Dispose();
        _encoder = null;
        _lossyTransport = null;
        _sink = null;
        _remoteMediaEndpoint = null;
    }

    private async void Shutdown()
    {
        StopMedia();

        _h264Decoder?.Dispose();
        _h264Decoder = null;
        _jpegDecoder.Dispose();

        if (_signaling is not null)
        {
            if (_activeCallId != Guid.Empty)
            {
                await _signaling.HangupAsync(_activeCallId);
            }

            await _signaling.DisconnectAsync();
        }
    }

    private void OnSendError(Exception ex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"Send error: {ex.Message}";
        });
    }

    private void OnKeyframeRequested()
    {
        _encoder?.ForceKeyframe();
    }

    private void OnCameraFailed(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"Camera error: {reason}";
        });
    }

    private void OnAudioChunk(byte[] chunk)
    {
        _mediaSession?.SendFrame(chunk, Protocol.Enums.FrameType.Audio, Protocol.Enums.VideoCodec.Pcm16);
    }

    private void OnCameraFrame(VideoFrame frame)
    {
        if (_encoder is null)
        {
            return;
        }

        (byte[] data, FrameType frameType) = _encoder.Encode(frame);
        System.Threading.Interlocked.Increment(ref _framesSent);
        _mediaSession?.SendFrame(data, frameType, _videoCodec);

        Dispatcher.BeginInvoke(() => WriteBitmap(ref _localBitmap, LocalImage, frame));
    }

    private void OnRemoteFrame(VideoFrame frame)
    {
        System.Threading.Interlocked.Increment(ref _framesReceived);
        Dispatcher.BeginInvoke(() => WriteBitmap(ref _remoteBitmap, RemoteImage, frame));
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

    private void UpdateMediaStatus()
    {
        int sentFps = (_framesSent - _sentLastTick) * 2;
        int receivedFps = (_framesReceived - _receivedLastTick) * 2;
        _sentLastTick = _framesSent;
        _receivedLastTick = _framesReceived;

        StatusText.Text = $"In call. sent {_framesSent} ({sentFps} fps), received {_framesReceived} ({receivedFps} fps), dgrams-in {_mediaSession?.ReceivedDatagrams ?? 0}, to {_remoteMediaEndpoint}, loss {_lossyTransport?.DropPercent ?? 0}%";
    }

    public void OnDisconnected()
    {
        Dispatcher.BeginInvoke(() =>
        {
            StopMedia();
            _activeCallId = Guid.Empty;
            _incomingCallId = Guid.Empty;
            CallButton.IsEnabled = false;
            AcceptButton.IsEnabled = false;
            RejectButton.IsEnabled = false;
            HangupButton.IsEnabled = false;
            RegisterButton.IsEnabled = true;
            StatusText.Text = "Disconnected from server.";
        });
    }

    public void OnRegisterAck(RegisterAckMessage message)
    {
    }

    public void OnCallRequestAck(CallRequestAckMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "Ringing...";
        });
    }

    public void OnIncomingCall(IncomingCallMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _incomingCallId = message.CallId;
            _remoteMediaEndpoint = new IPEndPoint(IPAddress.Parse(message.Ip), message.Port);
            AcceptButton.IsEnabled = true;
            RejectButton.IsEnabled = true;
            StatusText.Text = $"Incoming call from '{message.CallerId}'.";
        });
    }

    public void OnCallAccepted(CallAcceptMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _activeCallId = message.CallId;
            _remoteMediaEndpoint = new IPEndPoint(IPAddress.Parse(message.Ip), message.Port);

            if (_remoteMediaEndpoint is not null)
            {
                StartMedia(_remoteMediaEndpoint);
            }

            CallButton.IsEnabled = false;
            StatusText.Text = "Call established.";
        });
    }

    public void OnCallRejected(CallRejectMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _activeCallId = Guid.Empty;
            CallButton.IsEnabled = true;
            StatusText.Text = $"Call rejected: {message.Reason}";
        });
    }

    public void OnCallHangup(HangupMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StopMedia();
            _activeCallId = Guid.Empty;
            HangupButton.IsEnabled = false;
            CallButton.IsEnabled = true;
            StatusText.Text = "Remote side hung up.";
        });
    }

    public void OnKeepAlive()
    {
    }

    private sealed class DecoderSink : IFrameSink
    {
        private readonly MainWindow _owner;

        public DecoderSink(MainWindow owner)
        {
            _owner = owner;
        }

        public void OnFrameReceived(ReadOnlyMemory<byte> data, FrameType frameType, uint sequence, VideoCodec videoCodec)
        {
            try
            {
                if (frameType == FrameType.Audio)
                {
                    _owner._audioPlayer?.Play(data.ToArray());
                    return;
                }

                IVideoDecoder decoder = videoCodec == VideoCodec.H264
                    ? (_owner._h264Decoder ??= new H264VideoDecoder())
                    : _owner._jpegDecoder;

                VideoFrame? frame = decoder.Decode(data.ToArray(), frameType);

                if (frame is not null)
                {
                    _owner.OnRemoteFrame(frame);
                }
            }
            catch (Exception ex)
            {
                _owner.Dispatcher.BeginInvoke(() =>
                {
                    _owner.StatusText.Text = $"Decode error: {ex.Message}";
                });
            }
        }
    }
}
