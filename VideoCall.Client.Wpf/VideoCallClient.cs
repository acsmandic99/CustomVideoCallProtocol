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

namespace VideoCall.Client.Wpf;

public sealed class VideoCallClient : ISignalingListener, IDisposable
{
    public enum SourceKind
    {
        WebCamera = 0,
        Synthetic = 1,
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

    private string _myName = string.Empty;
    private ushort _localUdpPort;
    private Guid _activeCallId;
    private Guid _incomingCallId;
    private IPEndPoint? _remoteEndpoint;
    private SourceKind _source = SourceKind.WebCamera;
    private VideoCodec _codec = VideoCodec.H264;
    private int _lossPercent;
    private int _captureWidth = 640;
    private int _captureHeight = 480;
    private int _captureFps = 30;

    public event Action<IncomingCallMessage>? IncomingCall;
    public event Action? CallEstablished;
    public event Action<string>? CallEnded;
    public event Action<VideoFrame>? RemoteVideoFrame;
    public event Action<byte[]>? RemoteAudioChunk;
    public event Action? DisconnectedFromServer;

    public bool IsInCall => _activeCallId != Guid.Empty;

    public void Configure(SourceKind source, VideoCodec codec, int lossPercent, int width = 640, int height = 480, int fps = 30)
    {
        _source = source;
        _codec = codec;
        _lossPercent = lossPercent;
        _captureWidth = width;
        _captureHeight = height;
        _captureFps = fps;
    }

    public async Task ConnectAndRegisterAsync(string serverHost, int serverPort, string userId)
    {
        var codec = new BinaryMessageCodec(new DefaultSignalingMessageFactory());
        _signaling = new SignalingClient(codec, this, NullLogger<SignalingClient>.Instance);
        _localUdpPort = (ushort)Random.Shared.Next(20000, 25000);

        await _signaling.ConnectAsync(serverHost, serverPort);
        bool ok = await _signaling.RegisterAsync(userId);

        if (!ok)
        {
            await _signaling.DisconnectAsync();
            _signaling = null;
            throw new InvalidOperationException("Registration failed: name already taken.");
        }

        _myName = userId;
    }

    public async Task<Guid> CallAsync(string calleeId)
    {
        if (_signaling is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        _activeCallId = await _signaling.CallAsync(calleeId, _signaling.LocalIp ?? "127.0.0.1", _localUdpPort);
        return _activeCallId;
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
        CallEnded?.Invoke("hung up");
    }

    public async Task DisconnectAsync()
    {
        StopMedia();

        if (_signaling is not null)
        {
            if (_activeCallId != Guid.Empty)
            {
                await _signaling.HangupAsync(_activeCallId);
            }

            await _signaling.DisconnectAsync();
            _signaling = null;
        }
    }

    private void StartMedia()
    {
        _encoder = _codec == VideoCodec.H264
            ? new H264VideoEncoder(_captureWidth, _captureHeight, _captureFps)
            : new JpegVideoEncoder();

        _lossyTransport = new LossyTransportDecorator(new UdpMediaTransport(), _lossPercent);
        _mediaSession = new MediaSession(_lossyTransport, _remoteEndpoint!, new Sink(this));
        _mediaSession.KeyframeRequested += () => _encoder?.ForceKeyframe();
        _mediaSession.Start(_localUdpPort);

        _camera = _source == SourceKind.Synthetic ? new SyntheticCamera() : new OpenCvCamera();
        _camera.FrameCaptured += OnFrame;
        _camera.Failed += OnCameraFailed;
        _camera.Start(_captureWidth, _captureHeight, _captureFps);

        _audioCapture = new AudioCapture();
        _audioCapture.ChunkCaptured += chunk => _mediaSession?.SendFrame(chunk, FrameType.Audio, VideoCodec.Pcm16);
        _audioCapture.Start();

        _audioPlayer = new AudioPlayer();
        _audioPlayer.Start();

        CallEstablished?.Invoke();
    }

    private void StopMedia()
    {
        if (_camera is not null)
        {
            _camera.FrameCaptured -= OnFrame;
            _camera.Failed -= OnCameraFailed;
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

    private void OnFrame(VideoFrame frame)
    {
        if (_encoder is null)
        {
            return;
        }

        (byte[] data, FrameType type) = _encoder.Encode(frame);
        _mediaSession?.SendFrame(data, type, _codec);
        OnLocalFrame?.Invoke(frame);
    }

    private event Action<VideoFrame>? OnLocalFrame;

    public event Action<VideoFrame>? LocalVideoFrame
    {
        add => OnLocalFrame += value;
        remove => OnLocalFrame -= value;
    }

    private void OnCameraFailed(string reason)
    {
        CallEnded?.Invoke($"camera error: {reason}");
    }

    public void OnDisconnected()
    {
        StopMedia();
        _activeCallId = Guid.Empty;
        _incomingCallId = Guid.Empty;
        DisconnectedFromServer?.Invoke();
    }

    public void OnRegisterAck(RegisterAckMessage message)
    {
    }

    public void OnCallRequestAck(CallRequestAckMessage message)
    {
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
        CallEnded?.Invoke($"rejected: {message.Reason}");
    }

    public void OnCallHangup(HangupMessage message)
    {
        StopMedia();
        _activeCallId = Guid.Empty;
        CallEnded?.Invoke("remote hung up");
    }

    public void OnKeepAlive()
    {
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
                    _owner.RemoteAudioChunk?.Invoke(data.ToArray());
                    return;
                }

                IVideoDecoder decoder = videoCodec == VideoCodec.H264
                    ? (_owner._h264Decoder ??= new H264VideoDecoder())
                    : _owner._jpegDecoder;

                VideoFrame? frame = decoder.Decode(data.ToArray(), frameType);

                if (frame is not null)
                {
                    _owner.RemoteVideoFrame?.Invoke(frame);
                }
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        StopMedia();
        _h264Decoder?.Dispose();
        _jpegDecoder.Dispose();

        if (_signaling is not null)
        {
            _signaling.DisconnectAsync().GetAwaiter().GetResult();
        }
    }
}
