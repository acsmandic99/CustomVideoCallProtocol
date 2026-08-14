using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public interface ISignalingListener
{
    void OnClientRegistered(string userId);

    void OnCallIncoming(CallRequestMessage message);

    void OnCallAccepted(CallAcceptMessage message);

    void OnCallRejected(CallRejectMessage message);

    void OnCallHangup(HangupMessage message);

    void OnKeepAlive();
}
