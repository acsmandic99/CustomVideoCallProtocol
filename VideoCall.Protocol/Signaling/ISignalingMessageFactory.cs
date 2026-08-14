using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public interface ISignalingMessageFactory
{
    ISignalingMessage Create(MessageType messageType);
}
