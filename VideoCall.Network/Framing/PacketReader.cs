using System.Buffers.Binary;
using VideoCall.Protocol.Enums;

namespace VideoCall.Network.Framing;

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

        if (payloadLength > 0)
        {
            var payload = new byte[payloadLength];
            buffer.Slice(Packet.HeaderSize, (int)payloadLength).CopyTo(payload);
            packet = new Packet
            {
                Version = version,
                MessageType = messageType,
                FrameType = frameType,
                Sequence = sequence,
                Payload = payload,
            };
        }
        else
        {
            packet = new Packet
            {
                Version = version,
                MessageType = messageType,
                FrameType = frameType,
                Sequence = sequence,
                Payload = Array.Empty<byte>(),
            };
        }

        return true;
    }
}
