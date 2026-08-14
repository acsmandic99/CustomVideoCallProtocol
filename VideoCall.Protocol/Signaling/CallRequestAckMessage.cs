using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class CallRequestAckMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.CallRequestAck;

    public Guid CallId { get; set; }
    public string CalleeId { get; set; } = string.Empty;

    public CallRequestAckMessage() { }

    public CallRequestAckMessage(Guid callId, string calleeId)
    {
        CallId = callId;
        CalleeId = calleeId;
    }
}
