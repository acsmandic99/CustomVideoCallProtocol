namespace VideoCall.Client;

/// <summary>Registration was rejected by the server; the message carries the server's reason.</summary>
public sealed class RegistrationFailedException : Exception
{
    public RegistrationFailedException(string reason) : base($"Registration failed: {reason}")
    {
    }
}
