using VideoCall.Protocol.Signaling;

namespace VideoCall.Network.Signaling;

public interface ISignalingListener
{
    void OnDisconnected();

    void OnRegisterAck(RegisterAckMessage message);

    void OnCallRequestAck(CallRequestAckMessage message);

    void OnIncomingCall(IncomingCallMessage message);

    void OnCallAccepted(CallAcceptMessage message);

    void OnCallRejected(CallRejectMessage message);

    void OnCallHangup(HangupMessage message);

    void OnKeepAlive();
}
