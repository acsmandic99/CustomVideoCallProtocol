using VideoCall.Protocol.Enums;

namespace VideoCall.Media.Test.Console;

public sealed class SyntheticFrameGenerator
{
    private readonly int _frameSize;
    private readonly int _keyframeInterval;
    private readonly object _lock = new();
    private readonly Random _random = new(1234);

    private int _framesSinceKeyframe = int.MaxValue;
    private bool _forceKeyframe;

    public SyntheticFrameGenerator(int frameSize, int keyframeInterval)
    {
        _frameSize = frameSize;
        _keyframeInterval = keyframeInterval;
    }

    public void RequestKeyframe()
    {
        lock (_lock)
        {
            _forceKeyframe = true;
        }
    }

    public (byte[] Data, FrameType FrameType, bool Forced) NextFrame()
    {
        lock (_lock)
        {
            bool forced = _forceKeyframe;
            _forceKeyframe = false;

            FrameType frameType;
            if (forced || _framesSinceKeyframe >= _keyframeInterval)
            {
                frameType = FrameType.Keyframe;
                _framesSinceKeyframe = 0;
            }
            else
            {
                frameType = FrameType.Delta;
                _framesSinceKeyframe++;
            }

            var data = new byte[_frameSize];
            _random.NextBytes(data);

            return (data, frameType, forced && frameType == FrameType.Keyframe);
        }
    }
}
