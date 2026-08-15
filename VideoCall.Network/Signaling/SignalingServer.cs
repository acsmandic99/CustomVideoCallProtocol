using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VideoCall.Network.Framing;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public sealed class SignalingServer : IDisposable
{
    private enum CallState : byte
    {
        Ringing = 1,
        Accepted = 2,
    }

    private sealed class CallEntry
    {
        public string CallerId { get; }
        public string CalleeId { get; }
        public CallState State { get; set; } = CallState.Ringing;
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        public CallEntry(string callerId, string calleeId)
        {
            CallerId = callerId;
            CalleeId = calleeId;
        }
    }

    private readonly IMessageCodec _codec;
    private readonly ILogger<SignalingServer> _logger;
    private readonly TimeSpan _ringingTimeout;

    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<Guid, CallEntry> _calls = new();

    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private Task? _sweepTask;

    public SignalingServer(IMessageCodec codec, ILogger<SignalingServer> logger, TimeSpan? ringingTimeout = null)
    {
        _codec = codec;
        _logger = logger;
        _ringingTimeout = ringingTimeout ?? TimeSpan.FromSeconds(30);
    }

    public Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = AcceptClientsAsync(_cts.Token);
        _sweepTask = SweepExpiredCallsAsync(_cts.Token);
        _logger.LogInformation("Signaling server started on port {Port} (ringing timeout {Timeout}s)", port, _ringingTimeout.TotalSeconds);
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
        if (_sweepTask is not null)
        {
            await Task.WhenAny(_sweepTask, Task.Delay(TimeSpan.FromSeconds(5)));
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

    private async Task SweepExpiredCallsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var pair in _calls)
                {
                    if (pair.Value.State == CallState.Ringing && DateTime.UtcNow - pair.Value.StartedAt > _ringingTimeout)
                    {
                        ExpireCall(pair.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ExpireCall(Guid callId)
    {
        if (!_calls.TryRemove(callId, out CallEntry? call))
        {
            return;
        }

        if (call.State != CallState.Ringing)
        {
            _calls[callId] = call;
            return;
        }

        _logger.LogInformation("Call {CallId} expired without answer", callId);

        if (_clients.TryGetValue(call.CallerId, out ClientConnection? caller))
        {
            ForwardTo(caller, new CallRejectMessage(callId, "no answer"));
        }

        if (_clients.TryGetValue(call.CalleeId, out ClientConnection? callee))
        {
            ForwardTo(callee, new HangupMessage(callId));
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
                HandleCallAccept(sender, m);
                break;
            case CallRejectMessage m:
                HandleCallReject(sender, m);
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

        bool success = !_clients.ContainsKey(message.UserId);
        string reason = success ? string.Empty : "UserId already registered";

        if (success)
        {
            sender.UserId = message.UserId;
            _clients[message.UserId] = sender;
            _logger.LogInformation("Client registered: {UserId}", message.UserId);
        }
        else
        {
            _logger.LogWarning("Registration failed for {UserId}: already taken", message.UserId);
        }

        var ack = new RegisterAckMessage(success, message.UserId, reason);
        ForwardTo(sender, ack);
    }

    private bool IsUserInCall(string userId)
    {
        foreach (var call in _calls.Values)
        {
            if (call.CallerId == userId || call.CalleeId == userId)
            {
                return true;
            }
        }

        return false;
    }

    private void HandleCallRequest(ClientConnection sender, CallRequestMessage message)
    {
        Guid callId = Guid.NewGuid();
        string callerId = sender.UserId ?? string.Empty;

        var ack = new CallRequestAckMessage(callId, message.CalleeId);
        ForwardTo(sender, ack);

        string? rejectReason = null;

        if (message.CalleeId == callerId)
        {
            rejectReason = "Cannot call yourself";
        }
        else if (IsUserInCall(callerId))
        {
            rejectReason = "busy";
        }
        else if (!_clients.TryGetValue(message.CalleeId, out ClientConnection? _))
        {
            rejectReason = "User not found";
        }
        else if (IsUserInCall(message.CalleeId))
        {
            rejectReason = "busy";
        }

        if (rejectReason is not null)
        {
            _logger.LogWarning("CallRequest from {CallerId} to {CalleeId} rejected: {Reason}", callerId, message.CalleeId, rejectReason);
            ForwardTo(sender, new CallRejectMessage(callId, rejectReason));
            return;
        }

        _calls[callId] = new CallEntry(callerId, message.CalleeId);

        var callee = _clients[message.CalleeId];
        ForwardTo(callee, new IncomingCallMessage(callId, callerId, message.Ip, message.Port));
        _logger.LogInformation("Call {CallId} routed from {CallerId} to {CalleeId}", callId, callerId, message.CalleeId);
    }

    private void HandleCallAccept(ClientConnection sender, CallAcceptMessage message)
    {
        if (_calls.TryGetValue(message.CallId, out CallEntry? call))
        {
            if (sender.UserId != call.CalleeId)
            {
                _logger.LogWarning("Call {CallId} accept from non-callee {UserId}", message.CallId, sender.UserId);
                ForwardTo(sender, new CallRejectMessage(message.CallId, "call not found"));
                return;
            }

            if (call.State == CallState.Ringing)
            {
                call.State = CallState.Accepted;

                if (_clients.TryGetValue(call.CallerId, out ClientConnection? caller))
                {
                    ForwardTo(caller, message);
                    _logger.LogInformation("Call {CallId} accepted", message.CallId);
                }
            }
            else
            {
                _logger.LogWarning("Call {CallId} already accepted", message.CallId);
                ForwardTo(sender, new CallRejectMessage(message.CallId, "call not found"));
            }
        }
        else
        {
            _logger.LogWarning("Call {CallId} not found for accept", message.CallId);
            ForwardTo(sender, new CallRejectMessage(message.CallId, "call not found"));
        }
    }

    private void HandleCallReject(ClientConnection sender, CallRejectMessage message)
    {
        if (_calls.TryGetValue(message.CallId, out CallEntry? call))
        {
            if (call.CallerId == sender.UserId || call.CalleeId == sender.UserId)
            {
                if (_clients.TryGetValue(call.CallerId, out ClientConnection? caller))
                {
                    ForwardTo(caller, message);
                    _logger.LogInformation("Call {CallId} rejected: {Reason}", message.CallId, message.Reason);
                }

                _calls.TryRemove(message.CallId, out _);
            }
            else
            {
                _logger.LogWarning("Call {CallId} reject from non-participant {UserId}", message.CallId, sender.UserId);
            }
        }
        else
        {
            _logger.LogWarning("Call {CallId} not found for reject", message.CallId);
            ForwardTo(sender, new CallRejectMessage(message.CallId, "call not found"));
        }
    }

    private void HandleHangup(ClientConnection sender, HangupMessage message)
    {
        if (_calls.TryRemove(message.CallId, out CallEntry? call))
        {
            if (call.CallerId != sender.UserId && call.CalleeId != sender.UserId)
            {
                _calls[message.CallId] = call;
                _logger.LogWarning("Call {CallId} hangup from non-participant {UserId}", message.CallId, sender.UserId);
                return;
            }

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
        else
        {
            _logger.LogWarning("Call {CallId} not found for hangup", message.CallId);
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

            foreach (var pair in _calls)
            {
                bool isCaller = pair.Value.CallerId == connection.UserId;
                bool isCallee = pair.Value.CalleeId == connection.UserId;

                if (!isCaller && !isCallee)
                {
                    continue;
                }

                if (!_calls.TryRemove(pair.Key, out _))
                {
                    continue;
                }

                string otherId = isCaller ? pair.Value.CalleeId : pair.Value.CallerId;
                if (_clients.TryGetValue(otherId, out ClientConnection? other))
                {
                    ForwardTo(other, new HangupMessage(pair.Key));
                }
                _logger.LogInformation("Call {CallId} ended: {UserId} disconnected", pair.Key, connection.UserId);
            }

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
