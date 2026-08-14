using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class RegisterMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.Register;

    public string UserId { get; set; } = string.Empty;

    public RegisterMessage() { }

    public RegisterMessage(string userId)
    {
        UserId = userId;
    }
}
