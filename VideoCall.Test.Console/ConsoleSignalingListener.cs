using VideoCall.Network.Signaling;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Test.Console;

public sealed class ConsoleSignalingListener : ISignalingListener
{
    private readonly string _name;

    public ConsoleSignalingListener(string name)
    {
        _name = name;
    }

    public void OnDisconnected()
    {
        Print("<-", "DISCONNECTED");
    }

    public void OnRegisterAck(RegisterAckMessage message)
    {
        Print("<-", $"RegisterAck: success={message.Success}, userId={message.UserId}, reason='{message.Reason}'");
    }

    public void OnCallRequestAck(CallRequestAckMessage message)
    {
        Print("<-", $"CallRequestAck: callId={message.CallId}, callee={message.CalleeId}");
    }

    public void OnIncomingCall(IncomingCallMessage message)
    {
        Print("<-", $"IncomingCall: callId={message.CallId}, caller={message.CallerId}, udp={message.Ip}:{message.Port}");
    }

    public void OnCallAccepted(CallAcceptMessage message)
    {
        Print("<-", $"CallAccepted: callId={message.CallId}, udp={message.Ip}:{message.Port}");
    }

    public void OnCallRejected(CallRejectMessage message)
    {
        Print("<-", $"CallRejected: callId={message.CallId}, reason='{message.Reason}'");
    }

    public void OnCallHangup(HangupMessage message)
    {
        Print("<-", $"Hangup: callId={message.CallId}");
    }

    public void OnKeepAlive()
    {
        Print("<-", "KeepAlive");
    }

    public void PrintSent(string description)
    {
        Print("->", description);
    }

    private void Print(string direction, string description)
    {
        System.Console.WriteLine($"  [{_name}] {direction} {description}");
    }
}
