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
int port = args.Length > 1 && int.TryParse(args[1], out int parsedPort) && parsedPort is > 0 and < 65536 ? parsedPort : 5000;

var codec = new BinaryMessageCodec(new DefaultSignalingMessageFactory());
var server = new SignalingServer(codec, loggerFactory.CreateLogger<SignalingServer>(), TimeSpan.FromSeconds(timeoutSeconds));

await server.StartAsync(port);
Console.WriteLine($"Server listening on port {port} (ringing timeout {timeoutSeconds}s). Press Enter to stop.");

Console.ReadLine();

await server.StopAsync();
Console.WriteLine("Server stopped.");
