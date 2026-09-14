using System;
using System.Threading;
using System.Threading.Tasks;

namespace McpSense.Server.Authentication;

/// <summary>
/// Supplies the bearer token attached to each upstream request.
/// </summary>
/// <remarks>
/// Tokens are resolved per request rather than captured once, so an implementation can refresh an
/// expiring token without the rest of the server knowing. Whatever it returns is attached by the
/// dispatcher and never appears in a tool schema, so it never reaches the model.
/// </remarks>
public interface IAccessTokenProvider
{
    /// <summary>
    /// Returns the token to send, or <c>null</c> to send no <c>Authorization</c> header.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Returns a token that was supplied once at startup and never changes.
/// </summary>
public sealed class StaticAccessTokenProvider : IAccessTokenProvider
{
    private readonly string? _token;

    /// <summary>Creates a provider over a fixed token.</summary>
    public StaticAccessTokenProvider(string? token) => _token = string.IsNullOrWhiteSpace(token) ? null : token;

    /// <inheritdoc />
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_token);
}

/// <summary>
/// Thrown when no usable token is available and one cannot be obtained without a person present.
/// </summary>
public sealed class AccessTokenUnavailableException : Exception
{
    /// <summary>Creates a new instance.</summary>
    public AccessTokenUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new instance.</summary>
    public AccessTokenUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
