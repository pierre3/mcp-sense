using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace McpSense.Server.Authentication;

/// <summary>
/// Settings for the OAuth 2.0 authorization code flow against the upstream API.
/// </summary>
/// <remarks>
/// PKCE is always used and no client secret is required. McpSense runs on the user's machine, so it
/// is a public client: a secret shipped alongside it would not be a secret. A secret is accepted
/// for providers that still insist on one for confidential clients.
/// </remarks>
public sealed record OAuthClientOptions
{
    /// <summary>Where the user is sent to approve access.</summary>
    public required Uri AuthorizationEndpoint { get; init; }

    /// <summary>Where authorization codes and refresh tokens are exchanged for access tokens.</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>The client identifier registered with the provider.</summary>
    public required string ClientId { get; init; }

    /// <summary>The client secret, for providers that require a confidential client.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>The scopes to request.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>
    /// The loopback port the provider redirects back to. Zero picks a free port, which only works
    /// with providers that allow an arbitrary loopback port in the registered redirect URI.
    /// </summary>
    public int RedirectPort { get; init; }

    /// <summary>
    /// Identifies the stored token set. Distinct settings must not share stored tokens, so this
    /// covers the provider, the client and the scopes that were granted.
    /// </summary>
    public string StorageKey => string.Join(
        '|',
        AuthorizationEndpoint.GetLeftPart(UriPartial.Path),
        ClientId,
        string.Join(' ', Scopes));
}

/// <summary>A token set as returned by the provider.</summary>
public sealed record OAuthTokens
{
    /// <summary>The token sent as <c>Authorization: Bearer ...</c>.</summary>
    public required string AccessToken { get; init; }

    /// <summary>The token used to obtain a new access token, when the provider issued one.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>When the access token stops being valid, if the provider said.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The scopes actually granted, which may be narrower than those requested.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Whether the token should be refreshed now, allowing a margin so that a token does not expire
    /// between the check and the request reaching the API.
    /// </summary>
    /// <param name="margin">How long before expiry to treat the token as spent.</param>
    public bool IsExpired(TimeSpan margin) => ExpiresAt is { } expiry && DateTimeOffset.UtcNow + margin >= expiry;
}

/// <summary>
/// Persists token sets between runs.
/// </summary>
/// <remarks>
/// Storage has to survive process exit. A CLI process ends after every invocation, so an in-memory
/// store would send the user back through the browser on each start, which defeats the point of a
/// refresh token.
/// </remarks>
public interface ITokenStore
{
    /// <summary>Loads the tokens stored under a key, or <c>null</c> when there are none.</summary>
    ValueTask<OAuthTokens?> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores tokens under a key, replacing anything already there.</summary>
    ValueTask SaveAsync(string key, OAuthTokens tokens, CancellationToken cancellationToken = default);

    /// <summary>Removes whatever is stored under a key.</summary>
    ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default);
}
