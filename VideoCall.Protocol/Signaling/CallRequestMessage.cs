using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class CallRequestMessage : SignalingMessageBase
{
    public override MessageType MessageType => MessageType.CallRequest;

    public string CalleeId { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public ushort Port { get; set; }

    public CallRequestMessage() { }

    public CallRequestMessage(string calleeId, string ip, ushort port)
    {
        CalleeId = calleeId;
        Ip = ip;
        Port = port;
    }
}
