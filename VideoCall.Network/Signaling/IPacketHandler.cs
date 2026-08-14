using VideoCall.Network.Framing;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public interface IPacketHandler
{
    IPacketHandler? Next { get; set; }

    bool Handle(Packet packet, ISignalingMessage? decoded);
}
