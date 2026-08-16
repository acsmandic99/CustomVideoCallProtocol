using System.Net;
using VideoCall.Media;
using VideoCall.Media.Test.Console;
using VideoCall.Media.Testing;
using VideoCall.Media.Transport;
using VideoCall.Protocol.Enums;
using VideoCall.Protocol.Framing;

namespace VideoCall.Media.Test.Console;

public static class Program
{
    private const int FrameCount = 60;
    private const int FrameSize = 8 * 1024;
    private const int KeyframeInterval = 15;

    public static async Task Main()
    {
        await RunScenario(
            name: "Scenario 1: no loss",
            alicePort: 7000,
            bobPort: 7001,
            dropPercent: 0,
            dropPredicate: null,
            verbose: false);

        await RunScenario(
            name: "Scenario 2: 10% random loss",
            alicePort: 7002,
            bobPort: 7003,
            dropPercent: 10,
            dropPredicate: null,
            verbose: false);

        await RunScenario(
            name: "Scenario 3: targeted keyframe drop (seq=17)",
            alicePort: 7004,
            bobPort: 7005,
            dropPercent: 0,
            dropPredicate: p => p.MessageType == MessageType.MediaFrame && p.FrameType == FrameType.Keyframe && p.Sequence == 17,
            verbose: true);

        System.Console.WriteLine();
        System.Console.WriteLine("=== All scenarios complete ===");
    }

    private static async Task RunScenario(string name, ushort alicePort, ushort bobPort, int dropPercent, Func<Packet, bool>? dropPredicate, bool verbose)
    {
        System.Console.WriteLine();
        System.Console.WriteLine($"=== {name} ===");

        var generator = new SyntheticFrameGenerator(FrameSize, KeyframeInterval);
        var aliceTransport = new LossyTransportDecorator(new UdpMediaTransport(), dropPercent, seed: 7, dropPredicate);
        var bobTransport = new UdpMediaTransport();

        var bobEndpoint = new IPEndPoint(IPAddress.Loopback, bobPort);
        var aliceEndpoint = new IPEndPoint(IPAddress.Loopback, alicePort);

        var bobSink = new CountingFrameSink("BOB", verbose);

        using var alice = new MediaSession(aliceTransport, bobEndpoint, new NullFrameSink());
        using var bob = new MediaSession(bobTransport, aliceEndpoint, bobSink);

        alice.KeyframeRequested += () =>
        {
            System.Console.WriteLine("  [ALICE] <- KEYFRAME REQUEST -> forcing next frame to be keyframe");
            generator.RequestKeyframe();
        };

        alice.Start(alicePort);
        bob.Start(bobPort);

        int sentKeyframes = 0;
        int forcedKeyframes = 0;

        for (int i = 0; i < FrameCount; i++)
        {
            (byte[] data, FrameType frameType, bool forced) = generator.NextFrame();

            if (frameType == FrameType.Keyframe)
            {
                sentKeyframes++;
            }
            if (forced)
            {
                forcedKeyframes++;
            }

            if (verbose)
            {
                System.Console.WriteLine($"  [ALICE] -> frame #{i + 1} type={frameType}{(forced ? " (forced)" : "")}");
            }

            alice.SendFrame(data, frameType, VideoCodec.Jpeg);
            await Task.Delay(5);
        }

        await Task.Delay(1500);

        System.Console.WriteLine($"  Result: sent={FrameCount} (keyframes={sentKeyframes}, forced={forcedKeyframes}), " +
                                 $"received complete={bobSink.ReceivedCount} (keyframes={bobSink.KeyframeCount}), " +
                                 $"dropped datagrams={aliceTransport.DroppedCount}, " +
                                 $"keyframe requests sent by BOB={bob.KeyframeRequestCount}");
    }
}
