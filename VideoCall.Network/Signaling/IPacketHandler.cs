using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public interface IPacketHandler
{
    IPacketHandler? Next { get; set; }

    bool Handle(Packet packet, ISignalingMessage? decoded);
}
