using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class DefaultSignalingMessageFactory : ISignalingMessageFactory
{
    public ISignalingMessage Create(MessageType messageType)
    {
        return messageType switch
        {
            MessageType.Register => new RegisterMessage(),
            MessageType.RegisterAck => new RegisterAckMessage(),
            MessageType.CallRequest => new CallRequestMessage(),
            MessageType.CallRequestAck => new CallRequestAckMessage(),
            MessageType.IncomingCall => new IncomingCallMessage(),
            MessageType.CallAccept => new CallAcceptMessage(),
            MessageType.CallReject => new CallRejectMessage(),
            MessageType.Hangup => new HangupMessage(),
            MessageType.KeepAlive => new KeepAliveMessage(),
            _ => throw new NotSupportedException($"Unsupported message type: {messageType}"),
        };
    }
}
