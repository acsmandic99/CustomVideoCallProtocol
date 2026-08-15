namespace VideoCall.Protocol.Enums;

public enum MessageType : byte
{
    Register = 1,
    CallRequest = 2,
    CallAccept = 3,
    CallReject = 4,
    Hangup = 5,
    KeepAlive = 6,
    MediaFrame = 8,
    RegisterAck = 9,
    CallRequestAck = 10,
    IncomingCall = 11,
    KeyframeRequest = 12,
}
