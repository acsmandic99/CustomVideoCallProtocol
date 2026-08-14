using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public abstract class SignalingMessageBase : ISignalingMessage
{
    public abstract MessageType MessageType { get; }
}
