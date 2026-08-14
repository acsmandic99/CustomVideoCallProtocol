using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class HangupMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.Hangup;

    public Guid CallId { get; set; }

    public HangupMessage() { }

    public HangupMessage(Guid callId)
    {
        CallId = callId;
    }
}
