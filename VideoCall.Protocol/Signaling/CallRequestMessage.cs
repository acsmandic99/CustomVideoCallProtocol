using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class CallRequestMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.CallRequest;

    public string CallerId { get; set; } = string.Empty;
    public string CalleeId { get; set; } = string.Empty;
    public uint CallId { get; set; }
    public string Ip { get; set; } = string.Empty;
    public ushort Port { get; set; }

    public CallRequestMessage() { }

    public CallRequestMessage(string callerId, string calleeId, uint callId, string ip, ushort port)
    {
        CallerId = callerId;
        CalleeId = calleeId;
        CallId = callId;
        Ip = ip;
        Port = port;
    }
}
