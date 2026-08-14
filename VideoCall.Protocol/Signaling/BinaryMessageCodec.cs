using System.Buffers.Binary;
using System.Text;
using VideoCall.Protocol.Enums;

namespace VideoCall.Protocol.Signaling;

public sealed class BinaryMessageCodec : IMessageCodec
{
    private readonly ISignalingMessageFactory _factory;

    public BinaryMessageCodec(ISignalingMessageFactory factory)
    {
        _factory = factory;
    }

    public byte[] Encode(ISignalingMessage message)
    {
        return message switch
        {
            RegisterMessage m => EncodeRegister(m),
            CallRequestMessage m => EncodeCallRequest(m),
            CallAcceptMessage m => EncodeCallAccept(m),
            CallRejectMessage m => EncodeCallReject(m),
            HangupMessage m => EncodeHangup(m),
            KeepAliveMessage => Array.Empty<byte>(),
            _ => throw new NotSupportedException($"Unsupported message type: {message.MessageType}"),
        };
    }

    public ISignalingMessage Decode(MessageType messageType, ReadOnlySpan<byte> payload)
    {
        var message = _factory.Create(messageType);

        return messageType switch
        {
            MessageType.Register => DecodeRegister((RegisterMessage)message, payload),
            MessageType.CallRequest => DecodeCallRequest((CallRequestMessage)message, payload),
            MessageType.CallAccept => DecodeCallAccept((CallAcceptMessage)message, payload),
            MessageType.CallReject => DecodeCallReject((CallRejectMessage)message, payload),
            MessageType.Hangup => DecodeHangup((HangupMessage)message, payload),
            MessageType.KeepAlive => message,
            _ => throw new NotSupportedException($"Unsupported message type: {messageType}"),
        };
    }

    private static void WriteString(Span<byte> buffer, ref int offset, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), (ushort)bytes.Length);
        offset += 2;
        bytes.CopyTo(buffer.Slice(offset));
        offset += bytes.Length;
    }

    private static string ReadString(ReadOnlySpan<byte> buffer, ref int offset)
    {
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
        offset += 2;
        string value = Encoding.UTF8.GetString(buffer.Slice(offset, length));
        offset += length;
        return value;
    }

    private static void WriteUInt32(Span<byte> buffer, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(offset, 4), value);
        offset += 4;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static void WriteUInt16(Span<byte> buffer, ref int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), value);
        offset += 2;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> buffer, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
        offset += 2;
        return value;
    }

    private static int StringSize(string value)
    {
        return 2 + Encoding.UTF8.GetByteCount(value);
    }

    private static byte[] EncodeRegister(RegisterMessage m)
    {
        int size = StringSize(m.UserId);
        var buffer = new byte[size];
        int offset = 0;
        WriteString(buffer, ref offset, m.UserId);
        return buffer;
    }

    private static RegisterMessage DecodeRegister(RegisterMessage m, ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        m.UserId = ReadString(payload, ref offset);
        return m;
    }

    private static byte[] EncodeCallRequest(CallRequestMessage m)
    {
        int size = StringSize(m.CallerId) + StringSize(m.CalleeId) + 4 + StringSize(m.Ip) + 2;
        var buffer = new byte[size];
        int offset = 0;
        WriteString(buffer, ref offset, m.CallerId);
        WriteString(buffer, ref offset, m.CalleeId);
        WriteUInt32(buffer, ref offset, m.CallId);
        WriteString(buffer, ref offset, m.Ip);
        WriteUInt16(buffer, ref offset, m.Port);
        return buffer;
    }

    private static CallRequestMessage DecodeCallRequest(CallRequestMessage m, ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        m.CallerId = ReadString(payload, ref offset);
        m.CalleeId = ReadString(payload, ref offset);
        m.CallId = ReadUInt32(payload, ref offset);
        m.Ip = ReadString(payload, ref offset);
        m.Port = ReadUInt16(payload, ref offset);
        return m;
    }

    private static byte[] EncodeCallAccept(CallAcceptMessage m)
    {
        int size = StringSize(m.CallerId) + StringSize(m.CalleeId) + 4 + StringSize(m.Ip) + 2;
        var buffer = new byte[size];
        int offset = 0;
        WriteString(buffer, ref offset, m.CallerId);
        WriteString(buffer, ref offset, m.CalleeId);
        WriteUInt32(buffer, ref offset, m.CallId);
        WriteString(buffer, ref offset, m.Ip);
        WriteUInt16(buffer, ref offset, m.Port);
        return buffer;
    }

    private static CallAcceptMessage DecodeCallAccept(CallAcceptMessage m, ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        m.CallerId = ReadString(payload, ref offset);
        m.CalleeId = ReadString(payload, ref offset);
        m.CallId = ReadUInt32(payload, ref offset);
        m.Ip = ReadString(payload, ref offset);
        m.Port = ReadUInt16(payload, ref offset);
        return m;
    }

    private static byte[] EncodeCallReject(CallRejectMessage m)
    {
        int size = StringSize(m.CallerId) + StringSize(m.CalleeId) + 4 + StringSize(m.Reason);
        var buffer = new byte[size];
        int offset = 0;
        WriteString(buffer, ref offset, m.CallerId);
        WriteString(buffer, ref offset, m.CalleeId);
        WriteUInt32(buffer, ref offset, m.CallId);
        WriteString(buffer, ref offset, m.Reason);
        return buffer;
    }

    private static CallRejectMessage DecodeCallReject(CallRejectMessage m, ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        m.CallerId = ReadString(payload, ref offset);
        m.CalleeId = ReadString(payload, ref offset);
        m.CallId = ReadUInt32(payload, ref offset);
        m.Reason = ReadString(payload, ref offset);
        return m;
    }

    private static byte[] EncodeHangup(HangupMessage m)
    {
        var buffer = new byte[4];
        int offset = 0;
        WriteUInt32(buffer, ref offset, m.CallId);
        return buffer;
    }

    private static HangupMessage DecodeHangup(HangupMessage m, ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        m.CallId = ReadUInt32(payload, ref offset);
        return m;
    }
}
