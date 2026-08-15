using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public sealed class SignalingPacketHandler : IPacketHandler
{
    private readonly IMessageCodec _codec;
    private readonly ISignalingListener _listener;

    public IPacketHandler? Next { get; set; }

    public SignalingPacketHandler(IMessageCodec codec, ISignalingListener listener)
    {
        _codec = codec;
        _listener = listener;
    }

    public bool Handle(Packet packet, ISignalingMessage? decoded)
    {
        if (packet.MessageType == MessageType.MediaFrame)
        {
            return Next?.Handle(packet, decoded) ?? false;
        }

        var message = decoded ?? _codec.Decode(packet.MessageType, packet.Payload);

        switch (message)
        {
            case RegisterAckMessage m:
                _listener.OnRegisterAck(m);
                break;
            case CallRequestAckMessage m:
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
            case RegisterMessage:
            case CallRequestMessage:
                break;
            default:
                return Next?.Handle(packet, message) ?? false;
        }

        return true;
    }
}
