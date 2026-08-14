using Microsoft.Extensions.Logging;
using VideoCall.Network.Signaling;
using VideoCall.Protocol.Signaling;

Console.WriteLine("=== VideoCall Signaling Server ===");

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        })
        .SetMinimumLevel(LogLevel.Information);
});

int timeoutSeconds = args.Length > 0 && int.TryParse(args[0], out int parsed) && parsed > 0 ? parsed : 30;

var codec = new BinaryMessageCodec(new DefaultSignalingMessageFactory());
var server = new SignalingServer(codec, loggerFactory.CreateLogger<SignalingServer>(), TimeSpan.FromSeconds(timeoutSeconds));

await server.StartAsync(5000);
Console.WriteLine($"Server listening on port 5000 (ringing timeout {timeoutSeconds}s). Press Enter to stop.");

Console.ReadLine();

await server.StopAsync();
Console.WriteLine("Server stopped.");
