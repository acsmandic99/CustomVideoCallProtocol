using System.Net;
using System.Net.Sockets;

namespace VideoCall.Media.Transport;

public sealed class UdpMediaTransport : IUdpMediaTransport
{
    private UdpClient? _udpClient;
    private Task? _receiveTask;
    private CancellationTokenSource _cts = new();

    public event DatagramReceivedHandler? DatagramReceived;

    public void Bind(ushort localPort)
    {
        _cts = new CancellationTokenSource();
        _udpClient = new UdpClient(localPort);
        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    public async Task SendToAsync(ReadOnlyMemory<byte> data, IPEndPoint remote)
    {
        if (_udpClient is null)
        {
            throw new InvalidOperationException("Transport is not bound");
        }

        await _udpClient.SendAsync(data, remote);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udpClient!.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                continue;
            }

            DatagramReceived?.Invoke(result.Buffer, result.RemoteEndPoint);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udpClient?.Close();
        _cts.Dispose();
    }
}
