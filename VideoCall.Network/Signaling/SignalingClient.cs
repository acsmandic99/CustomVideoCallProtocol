using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VideoCall.Network.Framing;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public sealed class SignalingClient : IDisposable
{
    private readonly IMessageCodec _codec;
    private readonly ISignalingListener _listener;
    private readonly ILogger<SignalingClient> _logger;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private TcpFramingReader _framingReader = new();
    private CancellationTokenSource _cts = new();
    private Task? _receiveTask;

    private TaskCompletionSource<bool>? _registerTcs;
    private TaskCompletionSource<Guid>? _callRequestTcs;

    public bool IsConnected => _tcpClient?.Connected ?? false;
    public string? LocalIp { get; private set; }

    public SignalingClient(IMessageCodec codec, ISignalingListener listener, ILogger<SignalingClient> logger)
    {
        _codec = codec;
        _listener = listener;
        _logger = logger;
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port, cancellationToken);
        var localEp = _tcpClient.Client.LocalEndPoint as System.Net.IPEndPoint;
        LocalIp = localEp?.Address.IsIPv4MappedToIPv6 == true ? localEp.Address.MapToIPv4().ToString() : localEp?.Address.ToString();
        _stream = _tcpClient.GetStream();
        _framingReader = new TcpFramingReader();
        _receiveTask = ReceiveLoopAsync(_cts.Token);
        _logger.LogInformation("Connected to {Host}:{Port}", host, port);
    }

    public async Task DisconnectAsync()
    {
        _cts.Cancel();

        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }
        _tcpClient?.Close();

        if (_receiveTask is not null)
        {
            await Task.WhenAny(_receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        }

        _logger.LogInformation("Disconnected");
    }

    public async Task<bool> RegisterAsync(string userId)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registerTcs = tcs;

        var message = new RegisterMessage(userId);
        await SendAsync(message);

        return await tcs.Task;
    }

    public async Task<Guid> CallAsync(string calleeId, string ip, ushort port)
    {
        var tcs = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _callRequestTcs = tcs;

        var message = new CallRequestMessage(calleeId, ip, port);
        await SendAsync(message);

        return await tcs.Task;
    }

    public async Task AcceptCallAsync(Guid callId, string ip, ushort port)
    {
        var message = new CallAcceptMessage(callId, ip, port);
        await SendAsync(message);
    }

    public async Task RejectCallAsync(Guid callId, string reason)
    {
        var message = new CallRejectMessage(callId, reason);
        await SendAsync(message);
    }

    public async Task HangupAsync(Guid callId)
    {
        var message = new HangupMessage(callId);
        await SendAsync(message);
    }

    public async Task SendKeepAliveAsync()
    {
        var message = new KeepAliveMessage();
        await SendAsync(message);
    }

    private async Task SendAsync(ISignalingMessage message)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Client is not connected");
        }

        byte[] payload = _codec.Encode(message);
        var packet = new Packet(message.MessageType, payload, frameType: FrameType.Audio);
        byte[] bytes = PacketWriter.Serialize(packet);

        await _sendLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(bytes);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await _stream!.ReadAsync(receiveBuffer, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                _framingReader.Append(receiveBuffer.AsSpan(0, read));

                while (_framingReader.TryRead(out Packet? packet))
                {
                    if (packet is not null)
                    {
                        HandlePacket(packet);
                    }
                }
            }
        }
        finally
        {
            _listener.OnDisconnected();
            CompletePendingRequests();
        }
    }

    private void HandlePacket(Packet packet)
    {
        ISignalingMessage? message;
        try
        {
            message = _codec.Decode(packet.MessageType, packet.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decode packet of type {MessageType}", packet.MessageType);
            return;
        }

        switch (message)
        {
            case RegisterAckMessage m:
                _registerTcs?.TrySetResult(m.Success);
                _registerTcs = null;
                _listener.OnRegisterAck(m);
                break;
            case CallRequestAckMessage m:
                _callRequestTcs?.TrySetResult(m.CallId);
                _callRequestTcs = null;
                _listener.OnCallRequestAck(m);
                break;
            case IncomingCallMessage m:
                _listener.OnIncomingCall(m);
                break;
            case CallAcceptMessage m:
                _listener.OnCallAccepted(m);
                break;
            case CallRejectMessage m:
                _listener.OnCallRejected(m);
                break;
            case HangupMessage m:
                _listener.OnCallHangup(m);
                break;
            case KeepAliveMessage:
                _listener.OnKeepAlive();
                break;
            default:
                _logger.LogWarning("Unknown message type {MessageType}", packet.MessageType);
                break;
        }
    }

    private void CompletePendingRequests()
    {
        _registerTcs?.TrySetException(new InvalidOperationException("Disconnected"));
        _registerTcs = null;
        _callRequestTcs?.TrySetException(new InvalidOperationException("Disconnected"));
        _callRequestTcs = null;
    }

    public void Dispose()
    {
        _cts.Dispose();
        _sendLock.Dispose();
        _tcpClient?.Dispose();
    }
}
