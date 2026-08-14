using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class CallRejectMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.CallReject;

    public string CallerId { get; set; } = string.Empty;
    public string CalleeId { get; set; } = string.Empty;
    public uint CallId { get; set; }
    public string Reason { get; set; } = string.Empty;

    public CallRejectMessage() { }

    public CallRejectMessage(string callerId, string calleeId, uint callId, string reason)
    {
        CallerId = callerId;
        CalleeId = calleeId;
        CallId = callId;
        Reason = reason;
    }
}
