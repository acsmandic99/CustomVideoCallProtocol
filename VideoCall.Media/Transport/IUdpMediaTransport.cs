using System.Net;

namespace VideoCall.Media.Transport;

public delegate void DatagramReceivedHandler(ReadOnlyMemory<byte> data, IPEndPoint from);

public interface IUdpMediaTransport : IDisposable
{
    event DatagramReceivedHandler? DatagramReceived;

    void Bind(ushort localPort);

    Task SendToAsync(ReadOnlyMemory<byte> data, IPEndPoint remote);
}
