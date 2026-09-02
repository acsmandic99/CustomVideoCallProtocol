using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace VideoCall.Benchmark.Core;

/// <summary>
/// Plain TCP baseline: each frame is sent as [4-byte big-endian length][payload] over a
/// loopback TCP connection. Frames arrive in order and without loss; the interesting
/// measurement is the per-frame latency (head-of-line blocking) compared to the UDP paths.
/// </summary>
public sealed class TcpFrameChannel : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TcpClient _sender;
    private readonly TcpClient _receiver;
    private readonly NetworkStream _senderStream;
    private readonly Task _receiveTask;
    private readonly Action<uint, long, long> _onFrame; // (sequence, bytes, receiveTick)

    private TcpFrameChannel(TcpListener listener, TcpClient sender, TcpClient receiver, Action<uint, long, long> onFrame)
    {
        _listener = listener;
        _sender = sender;
        _receiver = receiver;
        _senderStream = sender.GetStream();
        _onFrame = onFrame;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<TcpFrameChannel> ConnectAsync(Action<uint, long, long> onFrame)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        var sender = new TcpClient();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await sender.ConnectAsync(IPAddress.Loopback, port);

        TcpClient receiver = await acceptTask;
        listener.Stop();

        return new TcpFrameChannel(listener, sender, receiver, onFrame);
    }

    private async Task ReceiveLoopAsync()
    {
        await using NetworkStream stream = _receiver.GetStream();

        var lengthHeader = new byte[4];
        uint sequence = 0;

        try
        {
            while (true)
            {
                int read = 0;

                while (read < 4)
                {
                    int n = await stream.ReadAsync(lengthHeader.AsMemory(read, 4 - read));

                    if (n == 0)
                    {
                        return;
                    }

                    read += n;
                }

                int length = BinaryPrimitives.ReadInt32BigEndian(lengthHeader);

                if (length < 0)
                {
                    return;
                }

                var payload = new byte[length];
                read = 0;

                while (read < length)
                {
                    read += await stream.ReadAsync(payload.AsMemory(read));
                }

                sequence++;
                _onFrame(sequence, length, Stopwatch.GetTimestamp());
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task SendFrameAsync(byte[] payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await _senderStream.WriteAsync(header);
        await _senderStream.WriteAsync(payload);
    }

    public void CloseSender()
    {
        _senderStream.Dispose();
        _sender.Dispose();
    }

    public void Dispose()
    {
        CloseSender();

        try
        {
            _receiveTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _receiver.Dispose();
        _listener.Stop();
    }
}
