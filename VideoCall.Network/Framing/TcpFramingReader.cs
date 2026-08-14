using VideoCall.Protocol.Enums;

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

        if (_buffer.Count < Packet.HeaderSize)
        {
            return false;
        }

        var header = _buffer.ToArray();
        ushort magic = (ushort)((header[0] << 8) | header[1]);
        if (magic != Packet.Magic)
        {
            int magicIndex = FindMagic(header);
            if (magicIndex < 0)
            {
                _buffer.RemoveRange(0, _buffer.Count - 1);
                return false;
            }
            _buffer.RemoveRange(0, magicIndex);
            header = _buffer.ToArray();
        }

        if (_buffer.Count < Packet.HeaderSize)
        {
            return false;
        }

        uint payloadLength = ((uint)header[9] << 24) | ((uint)header[10] << 16) | ((uint)header[11] << 8) | header[12];
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

    private static int FindMagic(byte[] data)
    {
        for (int i = 1; i < data.Length - 1; i++)
        {
            if (data[i] == 0x56 && data[i + 1] == 0x43)
            {
                return i;
            }
        }
        return -1;
    }
}
