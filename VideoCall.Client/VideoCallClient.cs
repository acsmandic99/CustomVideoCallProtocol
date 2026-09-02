using System.Net;
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

namespace VideoCall.Client;

public sealed class VideoCallClient : ISignalingListener, IDisposable
{
    public enum SourceKind
    {
        WebCamera = 0,
        Synthetic = 1,
        VideoFile = 2,
    }

    private SignalingClient? _signaling;
    private MediaSession? _mediaSession;
    private LossyTransportDecorator? _lossyTransport;
    private ICamera? _camera;
    private AudioCapture? _audioCapture;
    private AudioPlayer? _audioPlayer;
    private IVideoEncoder? _encoder;
    private IVideoDecoder? _h264Decoder;
    private readonly IVideoDecoder _jpegDecoder = new JpegVideoDecoder();

    private Guid _activeCallId;
    private Guid _incomingCallId;
    private IPEndPoint? _remoteEndpoint;
    private ushort _localUdpPort;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;

    private SourceKind _source = SourceKind.WebCamera;
    private string _videoFilePath = string.Empty;
    private VideoCodec _codec = VideoCodec.H264;
    private int _lossPercent;
    private int _captureWidth = 640;
    private int _captureHeight = 480;
    private int _captureFps = 30;

    public event Action<IncomingCallMessage>? IncomingCall;
    public event Action? Ringing;
    public event Action? CallEstablished;
    public event Action<string>? CallRejected;
    public event Action<string>? CallEnded;
    public event Action<VideoFrame>? LocalVideoFrame;
    public event Action<VideoFrame>? RemoteVideoFrame;
    public event Action? DisconnectedFromServer;
    public event Action<string>? CameraError;
    public event Action<Exception>? SendError;
    public event Action<Exception>? DecodeError;

    public string RegisteredName { get; private set; } = string.Empty;
    public bool IsRegistered => _signaling is not null;
    public bool IsInCall => _activeCallId != Guid.Empty;
    public ushort MediaPort => _localUdpPort;
    public IPEndPoint? RemoteEndpoint => _remoteEndpoint;

    public int SentFrames { get; private set; }
    public int ReceivedFrames { get; private set; }
    public int EmptyEncodes { get; private set; }
    public int ReceivedDatagrams => _mediaSession?.ReceivedDatagrams ?? 0;
    public int RawReceived => _lossyTransport?.RawReceivedCount ?? 0;
    public int NackCount => _mediaSession?.NackCount ?? 0;
    public int KeyframeRequestCount => _mediaSession?.KeyframeRequestCount ?? 0;

    public int MicrophoneIndex { get; set; }

    private bool _microphoneMuted;
    private bool _cameraMuted;

    public bool MicrophoneMuted
    {
        get => _microphoneMuted;
        set => _microphoneMuted = value;
    }

    public bool CameraMuted
    {
        get => _cameraMuted;
        set
        {
            _cameraMuted = value;

            if (!value && _encoder is not null)
            {
                _encoder.ForceKeyframe();
            }
        }
    }

    public int DropPercent
    {
        get => _lossyTransport?.DropPercent ?? _lossPercent;
        set
        {
            _lossPercent = value;
            if (_lossyTransport is not null)
            {
                _lossyTransport.DropPercent = value;
            }
        }
    }

    public void Configure(SourceKind source, string videoFilePath, VideoCodec codec, int lossPercent, int width = 640, int height = 480, int fps = 30)
    {
        _source = source;
        _videoFilePath = videoFilePath;
        _codec = codec;
        _lossPercent = lossPercent;
        _captureWidth = width;
        _captureHeight = height;
        _captureFps = fps;
    }

    public async Task ConnectAndRegisterAsync(string serverHost, int serverPort, string userId)
    {
        var codecFactory = new BinaryMessageCodec(new DefaultSignalingMessageFactory());
        var guard = new ConnectionGuard(this);
        _signaling = new SignalingClient(codecFactory, guard, NullLogger<SignalingClient>.Instance);
        guard.Bind(_signaling);
        _localUdpPort = (ushort)Random.Shared.Next(20000, 25000);

        await _signaling.ConnectAsync(serverHost, serverPort);
        bool ok = await _signaling.RegisterAsync(userId);

        if (!ok)
        {
            SignalingClient? failed = _signaling;
            _signaling = null;
            await failed.DisconnectAsync();
            throw new InvalidOperationException("Registration failed: name already taken.");
        }

        RegisteredName = userId;
        StartKeepAlive();
    }

    public async Task CallAsync(string calleeId)
    {
        if (_signaling is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        if (calleeId == RegisteredName)
        {
            throw new InvalidOperationException("You cannot call yourself.");
        }

        _activeCallId = await _signaling.CallAsync(calleeId, _signaling.LocalIp ?? "127.0.0.1", _localUdpPort);
    }

    public async Task AcceptCallAsync()
    {
        if (_signaling is null || _incomingCallId == Guid.Empty)
        {
            return;
        }

        _activeCallId = _incomingCallId;
        _incomingCallId = Guid.Empty;
        await _signaling.AcceptCallAsync(_activeCallId, _signaling.LocalIp ?? "127.0.0.1", _localUdpPort);

        if (_remoteEndpoint is not null)
        {
            StartMedia();
        }
    }

    public async Task RejectCallAsync(string reason = "busy")
    {
        if (_signaling is null || _incomingCallId == Guid.Empty)
        {
            return;
        }

        await _signaling.RejectCallAsync(_incomingCallId, reason);
        _incomingCallId = Guid.Empty;
    }

    public async Task HangupAsync()
    {
        StopMedia();

        if (_signaling is not null && _activeCallId != Guid.Empty)
        {
            await _signaling.HangupAsync(_activeCallId);
        }

        _activeCallId = Guid.Empty;
        CallEnded?.Invoke("Hung up.");
    }

    public async Task DisconnectAsync()
    {
        StopMedia();

        SignalingClient? signaling = _signaling;
        _signaling = null;

        if (signaling is not null)
        {
            if (_activeCallId != Guid.Empty)
            {
                await signaling.HangupAsync(_activeCallId);
            }

            await signaling.DisconnectAsync();
        }

        _activeCallId = Guid.Empty;
        _incomingCallId = Guid.Empty;
        StopKeepAlive();
    }

    private void StartKeepAlive()
    {
        StopKeepAlive();
        _keepAliveCts = new CancellationTokenSource();
        var signaling = _signaling;
        var token = _keepAliveCts.Token;

        _keepAliveTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));

            try
            {
                while (await timer.WaitForNextTickAsync(token) && _signaling == signaling)
                {
                    await signaling.SendKeepAliveAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }, token);
    }

    private void StopKeepAlive()
    {
        _keepAliveCts?.Cancel();
        _keepAliveTask?.Wait(TimeSpan.FromSeconds(1));
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
        _keepAliveTask = null;
    }

    private void StartMedia()
    {
        int width = _captureWidth;
        int height = _captureHeight;
        int fps = _captureFps;

        if (_source == SourceKind.VideoFile)
        {
            var probe = FileVideoSource.Probe(_videoFilePath);

            if (probe is null)
            {
                CameraError?.Invoke($"Cannot open video file: {_videoFilePath}");
                _ = AbortCallAsync("video file error");
                return;
            }

            (width, height, fps) = probe.Value;
            _camera = new FileVideoSource(_videoFilePath);
        }
        else
        {
            _camera = _source == SourceKind.Synthetic ? new SyntheticCamera() : new OpenCvCamera();
        }

        _encoder = _codec == VideoCodec.H264
            ? new H264VideoEncoder(width, height, fps)
            : new JpegVideoEncoder();

        _lossyTransport = new LossyTransportDecorator(new UdpMediaTransport(), _lossPercent);
        _mediaSession = new MediaSession(_lossyTransport, _remoteEndpoint!, new Sink(this));
        _mediaSession.KeyframeRequested += () => _encoder?.ForceKeyframe();
        _mediaSession.SendError += ex => SendError?.Invoke(ex);
        _mediaSession.Start(_localUdpPort);

        _camera.FrameCaptured += OnFrame;
        _camera.Failed += reason => CameraError?.Invoke(reason);
        _camera.Start(width, height, fps);

        if (_camera is FileVideoSource fvs)
        {
            fvs.AudioCaptured += chunk => { if (!MicrophoneMuted) _mediaSession?.SendFrame(chunk, FrameType.Audio, VideoCodec.Pcm16); };
            fvs.AudioUnavailable += StartMicrophone;
        }
        else
        {
            StartMicrophone();
        }

        _audioPlayer = new AudioPlayer();

        SentFrames = 0;
        ReceivedFrames = 0;
        EmptyEncodes = 0;

        CallEstablished?.Invoke();
    }

    private void StartMicrophone()
    {
        _audioCapture = new AudioCapture(MicrophoneIndex);
        _audioCapture.ChunkCaptured += chunk => { if (!MicrophoneMuted) _mediaSession?.SendFrame(chunk, FrameType.Audio, VideoCodec.Pcm16); };
        _audioCapture.Start();
    }

    private void StopMedia()
    {
        if (_camera is not null)
        {
            _camera.Dispose();
            _camera = null;
        }

        _audioCapture?.Dispose();
        _audioCapture = null;
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        _encoder?.Dispose();
        _encoder = null;
        _mediaSession?.Dispose();
        _mediaSession = null;
        _lossyTransport = null;
        _remoteEndpoint = null;
    }

    private async Task AbortCallAsync(string reason)
    {
        StopMedia();

        if (_signaling is not null && _activeCallId != Guid.Empty)
        {
            await _signaling.HangupAsync(_activeCallId);
        }

        _activeCallId = Guid.Empty;
        _incomingCallId = Guid.Empty;
        CallEnded?.Invoke(reason);
    }

    private void OnFrame(VideoFrame frame)
    {
        if (_encoder is null || CameraMuted)
        {
            return;
        }

        (byte[] data, FrameType frameType) = _encoder.Encode(frame);

        if (data.Length == 0)
        {
            EmptyEncodes++;
            LocalVideoFrame?.Invoke(frame);
            return;
        }

        SentFrames++;
        _mediaSession?.SendFrame(data, frameType, _codec);
        LocalVideoFrame?.Invoke(frame);
    }

    public void OnDisconnected()
    {
        StopMedia();
        StopKeepAlive();
        _activeCallId = Guid.Empty;
        _incomingCallId = Guid.Empty;
        DisconnectedFromServer?.Invoke();
    }

    public void OnRegisterAck(RegisterAckMessage message)
    {
    }

    public void OnCallRequestAck(CallRequestAckMessage message)
    {
        Ringing?.Invoke();
    }

    public void OnIncomingCall(IncomingCallMessage message)
    {
        _incomingCallId = message.CallId;
        _remoteEndpoint = new IPEndPoint(IPAddress.Parse(message.Ip), message.Port);
        IncomingCall?.Invoke(message);
    }

    public void OnCallAccepted(CallAcceptMessage message)
    {
        _activeCallId = message.CallId;
        _remoteEndpoint = new IPEndPoint(IPAddress.Parse(message.Ip), message.Port);
        StartMedia();
    }

    public void OnCallRejected(CallRejectMessage message)
    {
        _activeCallId = Guid.Empty;
        CallRejected?.Invoke(message.Reason);
    }

    public void OnCallHangup(HangupMessage message)
    {
        StopMedia();
        _activeCallId = Guid.Empty;
        CallEnded?.Invoke("Remote side hung up.");
    }

    public void OnKeepAlive()
    {
    }

    private void OnRemoteFrame(VideoFrame frame)
    {
        ReceivedFrames++;
        RemoteVideoFrame?.Invoke(frame);
    }

    private sealed class Sink : IFrameSink
    {
        private readonly VideoCallClient _owner;

        public Sink(VideoCallClient owner)
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

                VideoFrame? frame = decoder.Decode(data.ToArray());

                if (frame is not null)
                {
                    _owner.OnRemoteFrame(frame);
                }
            }
            catch (Exception ex)
            {
                _owner.DecodeError?.Invoke(ex);
            }
        }
    }

    private sealed class ConnectionGuard : ISignalingListener
    {
        private readonly VideoCallClient _owner;
        private SignalingClient? _client;

        public ConnectionGuard(VideoCallClient owner)
        {
            _owner = owner;
        }

        public void Bind(SignalingClient client)
        {
            _client = client;
        }

        private bool IsActive => _client is not null && _owner._signaling == _client;

        public void OnDisconnected()
        {
            if (IsActive)
            {
                _owner.OnDisconnected();
            }
        }

        public void OnRegisterAck(RegisterAckMessage message)
        {
            if (IsActive)
            {
                _owner.OnRegisterAck(message);
            }
        }

        public void OnCallRequestAck(CallRequestAckMessage message)
        {
            if (IsActive)
            {
                _owner.OnCallRequestAck(message);
            }
        }

        public void OnIncomingCall(IncomingCallMessage message)
        {
            if (IsActive)
            {
                _owner.OnIncomingCall(message);
            }
        }

        public void OnCallAccepted(CallAcceptMessage message)
        {
            if (IsActive)
            {
                _owner.OnCallAccepted(message);
            }
        }

        public void OnCallRejected(CallRejectMessage message)
        {
            if (IsActive)
            {
                _owner.OnCallRejected(message);
            }
        }

        public void OnCallHangup(HangupMessage message)
        {
            if (IsActive)
            {
                _owner.OnCallHangup(message);
            }
        }

        public void OnKeepAlive()
        {
            if (IsActive)
            {
                _owner.OnKeepAlive();
            }
        }
    }

    public void Dispose()
    {
        StopMedia();
        StopKeepAlive();
        _h264Decoder?.Dispose();
        _jpegDecoder.Dispose();
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
