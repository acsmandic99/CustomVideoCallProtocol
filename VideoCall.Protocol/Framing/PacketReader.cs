using System.Buffers.Binary;
using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Framing;

public static class PacketReader
{
    public static bool TryParse(ReadOnlySpan<byte> buffer, out Packet? packet)
    {
        packet = null;

        if (buffer.Length < Packet.HeaderSize)
        {
            return false;
        }

        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));
        if (magic != Packet.Magic)
        {
            return false;
        }

        byte version = buffer[2];
        var messageType = (MessageType)buffer[3];
        var frameType = (FrameType)buffer[4];
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(5, 4));
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(9, 4));

        if (buffer.Length < Packet.HeaderSize + payloadLength)
        {
            return false;
        }

        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            buffer.Slice(Packet.HeaderSize, (int)payloadLength).CopyTo(payload);
        }

        packet = new Packet
        {
            Version = version,
            MessageType = messageType,
            FrameType = frameType,
            Sequence = sequence,
            Payload = payload,
        };

        return true;
    }
}
