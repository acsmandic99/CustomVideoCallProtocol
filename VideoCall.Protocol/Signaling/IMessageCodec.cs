using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public interface IMessageCodec
{
    byte[] Encode(ISignalingMessage message);

    ISignalingMessage Decode(MessageType messageType, ReadOnlySpan<byte> payload);
}
