namespace VideoCall.Protocol.Enums;

public enum FrameType : byte
{
    Audio = 0,
    Keyframe = 1,
    Delta = 2,

    /// <summary>Carried by control packets that have no media frame type.</summary>
    None = 3,
}
