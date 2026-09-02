using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Framing;

public sealed class Packet
{
    public const ushort Magic = 0x5643;
    public const byte CurrentVersion = 1;
    public const int HeaderSize = 13;
    public const int MaxPayloadSize = 64 * 1024;

    public byte Version { get; set; } = CurrentVersion;
    public MessageType MessageType { get; set; }
    public FrameType FrameType { get; set; }
    public uint Sequence { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public int TotalSize => HeaderSize + Payload.Length;

    public Packet() { }

    public Packet(MessageType messageType, byte[] payload, uint sequence = 0, FrameType frameType = FrameType.None)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new ArgumentException($"Payload exceeds the maximum of {MaxPayloadSize} bytes.", nameof(payload));
        }

        MessageType = messageType;
        Payload = payload;
        Sequence = sequence;
        FrameType = frameType;
    }
}
