namespace VideoCall.Protocol.Enums;

public enum MessageType : byte
{
    Register = 1,
    CallRequest = 2,
    CallAccept = 3,
    CallReject = 4,
    Hangup = 5,
    KeepAlive = 6,
    MediaFrame = 7,
    RegisterAck = 8,
    CallRequestAck = 9,
    IncomingCall = 10,
    KeyframeRequest = 11,
}
