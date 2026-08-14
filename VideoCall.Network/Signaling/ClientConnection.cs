using System.Net.Sockets;
using VideoCall.Network.Framing;

namespace VideoCall.Network.Signaling;

internal sealed class ClientConnection
{
    public TcpClient TcpClient { get; }
    public TcpFramingReader FramingReader { get; }
    public string? UserId { get; set; }

    public ClientConnection(TcpClient tcpClient)
    {
        TcpClient = tcpClient;
        FramingReader = new TcpFramingReader();
    }

    public NetworkStream GetStream()
    {
        return TcpClient.GetStream();
    }

    public void Close()
    {
        TcpClient.Close();
    }
}
