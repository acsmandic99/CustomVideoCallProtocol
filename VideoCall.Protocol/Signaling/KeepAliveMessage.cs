using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class KeepAliveMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.KeepAlive;
}
