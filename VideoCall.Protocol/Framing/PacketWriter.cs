using System.Buffers.Binary;
using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Framing;

public static class PacketWriter
{
    public static byte[] Serialize(Packet packet)
    {
        var buffer = new byte[packet.TotalSize];

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), Packet.Magic);
        buffer[2] = packet.Version;
        buffer[3] = (byte)packet.MessageType;
        buffer[4] = (byte)packet.FrameType;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5, 4), packet.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(9, 4), (uint)packet.Payload.Length);

        if (packet.Payload.Length > 0)
        {
            Buffer.BlockCopy(packet.Payload, 0, buffer, Packet.HeaderSize, packet.Payload.Length);
        }

        return buffer;
    }
}
