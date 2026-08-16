namespace VideoCall.Codecs;

public sealed record VideoFrame(byte[] Bgr24Data, int Width, int Height);
