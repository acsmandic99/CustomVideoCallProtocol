using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;

namespace VideoCall.Network.Framing;

public sealed class TcpFramingReader
{
    private readonly List<byte> _buffer = new();

    public void Append(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
    }

    public bool TryRead(out Packet? packet)
    {
        packet = null;

        if (!TrySyncToMagic())
        {
            return false;
        }

        uint payloadLength = ((uint)_buffer[9] << 24) | ((uint)_buffer[10] << 16) | ((uint)_buffer[11] << 8) | _buffer[12];

        if (payloadLength > Packet.MaxPayloadSize)
        {
            // corrupted length: the stream can no longer be trusted at this offset, resync
            _buffer.RemoveAt(0);
            return false;
        }

        int totalSize = Packet.HeaderSize + (int)payloadLength;

        if (_buffer.Count < totalSize)
        {
            return false;
        }

        var packetBytes = new byte[totalSize];
        _buffer.CopyTo(0, packetBytes, 0, totalSize);
        _buffer.RemoveRange(0, totalSize);

        return PacketReader.TryParse(packetBytes, out packet);
    }

    private bool TrySyncToMagic()
    {
        while (_buffer.Count >= Packet.HeaderSize)
        {
            if (_buffer[0] == (Packet.Magic >> 8) && _buffer[1] == (Packet.Magic & 0xFF))
            {
                return true;
            }

            int magicIndex = FindMagic(1);

            if (magicIndex < 0)
            {
                _buffer.RemoveRange(0, _buffer.Count - 1);
                return false;
            }

            _buffer.RemoveRange(0, magicIndex);
        }

        return false;
    }

    private int FindMagic(int start)
    {
        for (int i = start; i < _buffer.Count - 1; i++)
        {
            if (_buffer[i] == 0x56 && _buffer[i + 1] == 0x43)
            {
                return i;
            }
        }

        return -1;
    }
}
