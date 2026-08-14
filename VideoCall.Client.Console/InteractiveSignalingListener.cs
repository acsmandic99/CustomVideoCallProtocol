using VideoCall.Network.Signaling;
using VideoCall.Protocol.Signaling;

namespace VideoCall.Client.Console;

public sealed class InteractiveSignalingListener : ISignalingListener
{
    private readonly object _printLock = new();
    private readonly string _name;
    private bool _promptShown;

    public Guid? IncomingCallId { get; private set; }
    public string? IncomingCaller { get; private set; }
    public Guid? ActiveCallId { get; private set; }

    public InteractiveSignalingListener(string name)
    {
        _name = name;
    }

    public void SetActiveCall(Guid callId)
    {
        ActiveCallId = callId;
        IncomingCallId = null;
        IncomingCaller = null;
    }

    public void ClearCall()
    {
        ActiveCallId = null;
        IncomingCallId = null;
        IncomingCaller = null;
    }

    public void ShowPrompt()
    {
        lock (_printLock)
        {
            if (_promptShown)
            {
                return;
            }

            System.Console.Write($"[{_name}] > ");
            _promptShown = true;
        }
    }

    public void LineEntered()
    {
        lock (_printLock)
        {
            _promptShown = false;
        }
    }

    public void OnDisconnected()
    {
        Print("<-", "DISCONNECTED");
    }

    public void OnRegisterAck(RegisterAckMessage message)
    {
        Print("<-", $"RegisterAck success={message.Success} userId={message.UserId}");
    }

    public void OnCallRequestAck(CallRequestAckMessage message)
    {
        Print("<-", $"CallRequestAck callId={message.CallId} callee={message.CalleeId}");
    }

    public void OnIncomingCall(IncomingCallMessage message)
    {
        IncomingCallId = message.CallId;
        IncomingCaller = message.CallerId;
        Print("<-", $"IncomingCall callId={message.CallId} caller={message.CallerId} udp={message.Ip}:{message.Port}");
        PrintHint("type 'accept' or 'reject'");
    }

    public void OnCallAccepted(CallAcceptMessage message)
    {
        ActiveCallId = message.CallId;
        Print("<-", $"CallAccepted callId={message.CallId} udp={message.Ip}:{message.Port}");
    }

    public void OnCallRejected(CallRejectMessage message)
    {
        ClearCall();
        Print("<-", $"CallRejected callId={message.CallId} reason='{message.Reason}'");
    }

    public void OnCallHangup(HangupMessage message)
    {
        ClearCall();
        Print("<-", $"Hangup callId={message.CallId}");
    }

    public void OnKeepAlive()
    {
        Print("<-", "KeepAlive");
    }

    public void PrintSent(string description)
    {
        Print("->", description);
    }

    public void PrintInfo(string description)
    {
        Print("--", description);
    }

    private void PrintHint(string description)
    {
        Print("??", description);
    }

    private void Print(string direction, string description)
    {
        lock (_printLock)
        {
            if (_promptShown)
            {
                System.Console.WriteLine();
            }

            System.Console.WriteLine($"[{_name}] {direction} {description}");
            System.Console.Write($"[{_name}] > ");
            _promptShown = true;
        }
    }
}
