using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class IncomingCallMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.IncomingCall;

    public Guid CallId { get; set; }
    public string CallerId { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public ushort Port { get; set; }

    public IncomingCallMessage() { }

    public IncomingCallMessage(Guid callId, string callerId, string ip, ushort port)
    {
        CallId = callId;
        CallerId = callerId;
        Ip = ip;
        Port = port;
    }
}
