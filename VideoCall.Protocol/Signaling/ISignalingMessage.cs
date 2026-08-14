using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public interface ISignalingMessage
{
    MessageType MessageType { get; }
}
