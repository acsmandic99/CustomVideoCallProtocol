using System.Diagnostics;
using System.Net.Sockets;
using VideoCall.Network.Framing;

namespace VideoCall.Network.Signaling;

internal sealed class ClientConnection
{
    private readonly object _sendLock = new();

    public TcpClient TcpClient { get; }
    public TcpFramingReader FramingReader { get; }
    public string? UserId { get; set; }
    public long LastReceivedTicks { get; set; } = Stopwatch.GetTimestamp();

    public ClientConnection(TcpClient tcpClient)
    {
        TcpClient = tcpClient;
        FramingReader = new TcpFramingReader();
    }

    public NetworkStream GetStream()
    {
        return TcpClient.GetStream();
    }

    /// <summary>Serializes writes: frames may be forwarded from several client-handler threads.</summary>
    public void Send(byte[] bytes)
    {
        lock (_sendLock)
        {
            GetStream().Write(bytes);
        }
    }

    public void Close()
    {
        TcpClient.Close();
    }
}
