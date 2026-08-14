namespace VideoCall.Protocol.Enums;

public enum FrameType : byte
{
    Audio = 0,
    Keyframe = 1,
    Delta = 2,
}
