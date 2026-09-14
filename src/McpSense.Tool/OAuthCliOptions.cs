using McpSense.Server.Authentication;

namespace McpSense.Tool;

/// <summary>
/// The OAuth settings shared by the CLI commands, and the wiring that turns them into a provider.
/// </summary>
internal sealed class OAuthCliOptions
{
    /// <summary>Where the user approves access. OAuth stays off when null.</summary>
    internal string? AuthorizationEndpoint { get; init; }

    /// <summary>Where codes and refresh tokens are exchanged for access tokens.</summary>
    internal string? TokenEndpoint { get; init; }

    /// <summary>The client identifier registered with the provider.</summary>
    internal string? ClientId { get; init; }

    /// <summary>The client secret, for providers that require a confidential client.</summary>
    internal string? ClientSecret { get; init; }

    /// <summary>Scopes to request, comma or space separated.</summary>
    internal string? Scopes { get; init; }

    /// <summary>The fixed loopback port to redirect to, or zero to pick a free one.</summary>
    internal int RedirectPort { get; init; }

    /// <summary>Where token files live.</summary>
    internal string? TokenDirectory { get; init; }

    /// <summary>Whether OAuth was asked for at all.</summary>
    internal bool IsEnabled => !string.IsNullOrWhiteSpace(AuthorizationEndpoint);

    /// <summary>Opens the token store these settings use.</summary>
    internal ITokenStore CreateStore()
        => new FileTokenStore(TokenDirectory is { Length: > 0 } directory ? directory : FileTokenStore.DefaultDirectory);

    /// <summary>
    /// Validates the settings and turns them into client options.
    /// </summary>
    /// <exception cref="InvalidOperationException">A required setting is missing or malformed.</exception>
    internal OAuthClientOptions ToClientOptions()
    {
        var authorization = ParseEndpoint(AuthorizationEndpoint, "--oauth-authorization-endpoint");
        var token = ParseEndpoint(TokenEndpoint, "--oauth-token-endpoint");

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("--oauth-client-id is required when using OAuth.");
        }

        return new OAuthClientOptions
        {
            AuthorizationEndpoint = authorization,
            TokenEndpoint = token,
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            Scopes = SplitScopes(Scopes),
            RedirectPort = RedirectPort,
        };
    }

    private static Uri ParseEndpoint(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{optionName} is required when using OAuth.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException($"{optionName} '{value}' is not an absolute URL.");
        }

        return parsed;
    }

    /// <summary>Accepts either separator, because providers document scopes both ways.</summary>
    private static IReadOnlyList<string> SplitScopes(string? scopes)
        => string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
