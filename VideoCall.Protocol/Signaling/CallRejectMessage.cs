using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class CallRejectMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.CallReject;

    public Guid CallId { get; set; }
    public string Reason { get; set; } = string.Empty;

    public CallRejectMessage() { }

    public CallRejectMessage(Guid callId, string reason)
    {
        CallId = callId;
        Reason = reason;
    }
}
