using Microsoft.Extensions.Logging;
using VideoCall.Network.Signaling;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Test.Console;

public static class Program
{
    public static async Task Main()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                })
                .SetMinimumLevel(LogLevel.Warning);
        });

        var codec = new BinaryMessageCodec(new DefaultSignalingMessageFactory());
        var serverLogger = loggerFactory.CreateLogger<SignalingServer>();

        var server = new SignalingServer(codec, serverLogger);
        await server.StartAsync(5000);
        System.Console.WriteLine("=== Signaling server started on port 5000 ===\n");

        var aliceListener = new ConsoleSignalingListener("Alice");
        var bobListener = new ConsoleSignalingListener("Bob");

        var alice = new SignalingClient(codec, aliceListener, loggerFactory.CreateLogger<SignalingClient>());
        var bob = new SignalingClient(codec, bobListener, loggerFactory.CreateLogger<SignalingClient>());

        System.Console.WriteLine("--- Step 1: Connect ---");
        await alice.ConnectAsync("127.0.0.1", 5000);
        System.Console.WriteLine("  Alice connected to server");
        await bob.ConnectAsync("127.0.0.1", 5000);
        System.Console.WriteLine("  Bob connected to server\n");

        System.Console.WriteLine("--- Step 2: Register ---");
        aliceListener.PrintSent($"Register: userId=Alice");
        bool aliceRegistered = await alice.RegisterAsync("Alice");

        bobListener.PrintSent($"Register: userId=Bob");
        bool bobRegistered = await bob.RegisterAsync("Bob");

        System.Console.WriteLine($"  Result: Alice registered={aliceRegistered}, Bob registered={bobRegistered}\n");

        await Task.Delay(500);

        System.Console.WriteLine("--- Step 3: Alice calls Bob ---");
        aliceListener.PrintSent($"CallRequest: callee=Bob, udp=127.0.0.1:6000");
        Guid callId = await alice.CallAsync("Bob", "127.0.0.1", 6000);
        System.Console.WriteLine($"  Result: Alice got callId={callId}\n");

        await Task.Delay(500);

        System.Console.WriteLine("--- Step 4: Bob accepts the call ---");
        bobListener.PrintSent($"CallAccept: callId={callId}, udp=127.0.0.1:6001");
        await bob.AcceptCallAsync(callId, "127.0.0.1", 6001);

        await Task.Delay(500);

        System.Console.WriteLine("--- Step 5: Bob hangs up ---");
        bobListener.PrintSent($"Hangup: callId={callId}");
        await bob.HangupAsync(callId);

        await Task.Delay(500);

        System.Console.WriteLine("\n--- Step 6: Disconnect ---");
        await alice.DisconnectAsync();
        await bob.DisconnectAsync();
        await server.StopAsync();

        System.Console.WriteLine("\n=== Test complete ===");
    }
}
