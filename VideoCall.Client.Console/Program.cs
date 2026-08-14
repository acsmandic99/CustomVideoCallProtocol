using Microsoft.Extensions.Logging;
using VideoCall.Client.Console;
using VideoCall.Network.Signaling;
using VideoCall.Protocol.Signaling;

if (args.Length < 1)
{
    System.Console.WriteLine("Usage: VideoCall.Client.Console <userId> [serverHost] [serverPort]");
    return 1;
}

string userId = args[0];
string host = args.Length > 1 ? args[1] : "127.0.0.1";
int port = args.Length > 2 && int.TryParse(args[2], out int parsedPort) ? parsedPort : 5000;
string localIp = "127.0.0.1";
ushort udpPort = (ushort)Random.Shared.Next(20000, 25000);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddSimpleConsole(options => options.SingleLine = true)
        .SetMinimumLevel(LogLevel.Warning);
});

var codec = new BinaryMessageCodec(new DefaultSignalingMessageFactory());
var listener = new InteractiveSignalingListener(userId);
var client = new SignalingClient(codec, listener, loggerFactory.CreateLogger<SignalingClient>());

System.Console.WriteLine($"=== VideoCall Client: {userId} ===");
System.Console.WriteLine($"UDP placeholder address: {localIp}:{udpPort}");
System.Console.WriteLine();

await client.ConnectAsync(host, port);

listener.PrintSent($"Register userId={userId}");
bool registered = await client.RegisterAsync(userId);

if (!registered)
{
    listener.PrintInfo("Registration failed, userId already taken. Exiting.");
    await client.DisconnectAsync();
    return 1;
}

listener.PrintInfo($"Registered as {userId}. Type 'help' for commands.");

while (true)
{
    listener.ShowPrompt();
    string? line = System.Console.ReadLine();

    if (line is null)
    {
        break;
    }

    listener.LineEntered();

    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (parts.Length == 0)
    {
        continue;
    }

    switch (parts[0].ToLowerInvariant())
    {
        case "help":
            PrintHelp();
            break;

        case "call":
            if (parts.Length < 2)
            {
                listener.PrintInfo("Usage: call <userId>");
                break;
            }

            if (listener.ActiveCallId is not null)
            {
                listener.PrintInfo("Already in a call. Hang up first.");
                break;
            }

            if (parts[1].Equals(userId, StringComparison.OrdinalIgnoreCase))
            {
                listener.PrintInfo("You cannot call yourself.");
                break;
            }

            listener.PrintSent($"CallRequest callee={parts[1]} udp={localIp}:{udpPort}");
            Guid callId = await client.CallAsync(parts[1], localIp, udpPort);
            listener.SetActiveCall(callId);
            listener.PrintInfo($"Call pending, callId={callId}");
            break;

        case "accept":
            if (listener.IncomingCallId is null)
            {
                listener.PrintInfo("No incoming call to accept.");
                break;
            }

            listener.PrintSent($"CallAccept callId={listener.IncomingCallId} udp={localIp}:{udpPort}");
            Guid incomingId = listener.IncomingCallId.Value;
            await client.AcceptCallAsync(incomingId, localIp, udpPort);
            listener.SetActiveCall(incomingId);
            listener.PrintInfo($"Call established, callId={incomingId}");
            break;

        case "reject":
            if (listener.IncomingCallId is null)
            {
                listener.PrintInfo("No incoming call to reject.");
                break;
            }

            listener.PrintSent($"CallReject callId={listener.IncomingCallId} reason='busy'");
            await client.RejectCallAsync(listener.IncomingCallId.Value, "busy");
            listener.ClearCall();
            listener.PrintInfo("Call rejected.");
            break;

        case "hangup":
            if (listener.ActiveCallId is null)
            {
                listener.PrintInfo("No active call to hang up.");
                break;
            }

            listener.PrintSent($"Hangup callId={listener.ActiveCallId}");
            await client.HangupAsync(listener.ActiveCallId.Value);
            listener.ClearCall();
            listener.PrintInfo("Hung up.");
            break;

        case "quit":
            await GracefulShutdown();
            return 0;

        default:
            listener.PrintInfo($"Unknown command '{parts[0]}'. Type 'help' for commands.");
            break;
    }
}

await GracefulShutdown();
return 0;

async Task GracefulShutdown()
{
    if (listener.ActiveCallId is not null)
    {
        listener.PrintSent($"Hangup callId={listener.ActiveCallId}");
        await client.HangupAsync(listener.ActiveCallId.Value);
        listener.ClearCall();
    }

    if (listener.IncomingCallId is not null)
    {
        listener.PrintSent($"CallReject callId={listener.IncomingCallId} reason='leaving'");
        await client.RejectCallAsync(listener.IncomingCallId.Value, "leaving");
        listener.ClearCall();
    }

    listener.PrintInfo("Quitting.");
    await client.DisconnectAsync();
}

void PrintHelp()
{
    listener.PrintInfo("Commands: call <userId> | accept | reject | hangup | quit | help");
}
