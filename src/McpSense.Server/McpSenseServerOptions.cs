using System;
using System.Collections.Generic;
using McpSense.Server.Authentication;

namespace McpSense.Server;

/// <summary>
/// Runtime configuration for the MCP server: where the API lives and how to authenticate to it.
/// </summary>
/// <remarks>
/// Credentials are supplied once at startup and attached by the server. They are never placed in a
/// tool schema and never reach the model. Only static credentials are supported here; the OAuth 2.0
/// authorization code flow with PKCE is a later milestone.
/// </remarks>
public sealed class McpSenseServerOptions
{
    /// <summary>
    /// The base address every request is resolved against, typically the spec's first
    /// <c>servers</c> entry or an explicit override.
    /// </summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>A bearer token sent as <c>Authorization: Bearer ...</c>.</summary>
    /// <remarks>Ignored when <see cref="AccessTokenProvider"/> is set.</remarks>
    public string? BearerToken { get; init; }

    /// <summary>
    /// Supplies the bearer token per request, for credentials that change over time.
    /// </summary>
    /// <remarks>
    /// Set this to authenticate with OAuth, where the token has to be refreshed as it expires.
    /// It takes precedence over <see cref="BearerToken"/>.
    /// </remarks>
    public IAccessTokenProvider? AccessTokenProvider { get; init; }

    /// <summary>The header an API key is sent in, for example <c>X-Api-Key</c>.</summary>
    public string? ApiKeyHeaderName { get; init; }

    /// <summary>The API key value paired with <see cref="ApiKeyHeaderName"/>.</summary>
    public string? ApiKeyValue { get; init; }

    /// <summary>
    /// Headers attached to every request, on top of the credentials above.
    /// </summary>
    /// <remarks>
    /// Real APIs commonly require a static header that is not a credential — GitHub rejects any
    /// request without a <c>User-Agent</c>, and many APIs pin a version through a header of their
    /// own. Credentials are applied after these, so a header here cannot displace them.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }
}
