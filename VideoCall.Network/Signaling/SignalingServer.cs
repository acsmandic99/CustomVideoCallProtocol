using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VideoCall.Network.Framing;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public sealed class SignalingServer : IDisposable
{
    private readonly IMessageCodec _codec;
    private readonly ILogger<SignalingServer> _logger;

    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<uint, (string CallerId, string CalleeId)> _calls = new();

    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    public SignalingServer(IMessageCodec codec, ILogger<SignalingServer> logger)
    {
        _codec = codec;
        _logger = logger;
    }

    public Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = AcceptClientsAsync(_cts.Token);
        _logger.LogInformation("Signaling server started on port {Port}", port);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _listener?.Stop();

        foreach (var client in _clients.Values)
        {
            client.Close();
        }
        _clients.Clear();
        _calls.Clear();

        if (_acceptTask is not null)
        {
            await Task.WhenAny(_acceptTask, Task.Delay(TimeSpan.FromSeconds(5)));
        }

        _logger.LogInformation("Signaling server stopped");
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting TCP client");
                continue;
            }

            _ = HandleClientAsync(tcpClient, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var connection = new ClientConnection(tcpClient);
        var stream = connection.GetStream();
        var receiveBuffer = new byte[8192];

        _logger.LogDebug("Client connected from {RemoteEndPoint}", tcpClient.Client.RemoteEndPoint);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(receiveBuffer, cancellationToken);
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

                connection.FramingReader.Append(receiveBuffer.AsSpan(0, read));

                while (connection.FramingReader.TryRead(out Packet? packet))
                {
                    if (packet is not null)
                    {
                        HandlePacket(connection, packet);
                    }
                }
            }
        }
        finally
        {
            DisconnectClient(connection);
        }
    }

    private void HandlePacket(ClientConnection sender, Packet packet)
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
            case RegisterMessage m:
                HandleRegister(sender, m);
                break;
            case CallRequestMessage m:
                HandleCallRequest(sender, m);
                break;
            case CallAcceptMessage m:
                HandleCallAccept(m);
                break;
            case CallRejectMessage m:
                HandleCallReject(m);
                break;
            case HangupMessage m:
                HandleHangup(sender, m);
                break;
            case KeepAliveMessage:
                _logger.LogDebug("KeepAlive from {UserId}", sender.UserId ?? "unregistered");
                break;
            default:
                _logger.LogWarning("Unknown message type {MessageType} from {UserId}", packet.MessageType, sender.UserId);
                break;
        }
    }

    private void HandleRegister(ClientConnection sender, RegisterMessage message)
    {
        if (sender.UserId is not null)
        {
            _clients.TryRemove(sender.UserId, out _);
        }

        sender.UserId = message.UserId;
        _clients[message.UserId] = sender;
        _logger.LogInformation("Client registered: {UserId}", message.UserId);
    }

    private void HandleCallRequest(ClientConnection sender, CallRequestMessage message)
    {
        _calls[message.CallId] = (message.CallerId, message.CalleeId);

        if (_clients.TryGetValue(message.CalleeId, out ClientConnection? callee))
        {
            ForwardTo(callee, message);
            _logger.LogInformation("Call {CallId} routed from {CallerId} to {CalleeId}", message.CallId, message.CallerId, message.CalleeId);
        }
        else
        {
            _logger.LogWarning("Callee {CalleeId} not found for call {CallId}", message.CalleeId, message.CallId);
            var reject = new CallRejectMessage(message.CallerId, message.CalleeId, message.CallId, "User not found");
            ForwardTo(sender, reject);
            _calls.TryRemove(message.CallId, out _);
        }
    }

    private void HandleCallAccept(CallAcceptMessage message)
    {
        if (_clients.TryGetValue(message.CallerId, out ClientConnection? caller))
        {
            ForwardTo(caller, message);
            _logger.LogInformation("Call {CallId} accepted by {CalleeId}", message.CallId, message.CalleeId);
        }
        else
        {
            _logger.LogWarning("Caller {CallerId} not found for call {CallId}", message.CallerId, message.CallId);
        }
    }

    private void HandleCallReject(CallRejectMessage message)
    {
        if (_clients.TryGetValue(message.CallerId, out ClientConnection? caller))
        {
            ForwardTo(caller, message);
            _logger.LogInformation("Call {CallId} rejected by {CalleeId}: {Reason}", message.CallId, message.CalleeId, message.Reason);
        }

        _calls.TryRemove(message.CallId, out _);
    }

    private void HandleHangup(ClientConnection sender, HangupMessage message)
    {
        if (_calls.TryRemove(message.CallId, out var call))
        {
            string? otherId = null;
            if (sender.UserId == call.CallerId)
            {
                otherId = call.CalleeId;
            }
            else if (sender.UserId == call.CalleeId)
            {
                otherId = call.CallerId;
            }

            if (otherId is not null && _clients.TryGetValue(otherId, out ClientConnection? other))
            {
                ForwardTo(other, message);
            }
            _logger.LogInformation("Call {CallId} hung up by {UserId}", message.CallId, sender.UserId);
        }
    }

    private void ForwardTo(ClientConnection target, ISignalingMessage message)
    {
        byte[] payload = _codec.Encode(message);
        var packet = new Packet(message.MessageType, payload, frameType: FrameType.Audio);
        byte[] bytes = PacketWriter.Serialize(packet);

        try
        {
            target.GetStream().Write(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward message to {UserId}", target.UserId);
        }
    }

    private void DisconnectClient(ClientConnection connection)
    {
        if (connection.UserId is not null)
        {
            _clients.TryRemove(connection.UserId, out _);
            _logger.LogInformation("Client disconnected: {UserId}", connection.UserId);
        }
        connection.Close();
    }

    public void Dispose()
    {
        _cts.Dispose();
        _listener?.Dispose();
    }
}
