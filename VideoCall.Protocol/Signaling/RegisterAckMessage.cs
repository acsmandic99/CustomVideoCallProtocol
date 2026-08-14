using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class RegisterAckMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.RegisterAck;

    public bool Success { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public RegisterAckMessage() { }

    public RegisterAckMessage(bool success, string userId, string reason)
    {
        Success = success;
        UserId = userId;
        Reason = reason;
    }
}
