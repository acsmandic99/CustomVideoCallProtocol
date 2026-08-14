using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class CallAcceptMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.CallAccept;

    public string CallerId { get; set; } = string.Empty;
    public string CalleeId { get; set; } = string.Empty;
    public uint CallId { get; set; }
    public string Ip { get; set; } = string.Empty;
    public ushort Port { get; set; }

    public CallAcceptMessage() { }

    public CallAcceptMessage(string callerId, string calleeId, uint callId, string ip, ushort port)
    {
        CallerId = callerId;
        CalleeId = calleeId;
        CallId = callId;
        Ip = ip;
        Port = port;
    }
}
