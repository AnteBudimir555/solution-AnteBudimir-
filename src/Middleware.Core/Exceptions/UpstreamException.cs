namespace Middleware.Core.Exceptions;

/// <summary>
/// Thrown when an upstream product source fails (network error, timeout, non-2xx response other than
/// 404). Maps to HTTP 502 Bad Gateway so clients can distinguish an upstream failure from a bad
/// request on our side.
/// </summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(string message) : base(message)
    {
    }

    public UpstreamException(string message, Exception cause) : base(message, cause)
    {
    }
}
