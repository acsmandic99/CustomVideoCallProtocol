using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class HangupMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.Hangup;

    public uint CallId { get; set; }

    public HangupMessage() { }

    public HangupMessage(uint callId)
    {
        CallId = callId;
    }
}
