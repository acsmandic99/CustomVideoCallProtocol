using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class CallAcceptMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.CallAccept;

    public Guid CallId { get; set; }
    public string Ip { get; set; } = string.Empty;
    public ushort Port { get; set; }

    public CallAcceptMessage() { }

    public CallAcceptMessage(Guid callId, string ip, ushort port)
    {
        CallId = callId;
        Ip = ip;
        Port = port;
    }
}
