using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McpSense.Server.Authentication;

/// <summary>
/// Talks to an OAuth 2.0 token endpoint.
/// </summary>
public sealed class OAuthTokenClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Creates a client over the given <see cref="HttpClient"/>.</summary>
    public OAuthTokenClient(HttpClient httpClient)
        => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <summary>
    /// Exchanges an authorization code for tokens, proving possession of the PKCE verifier.
    /// </summary>
    public Task<OAuthTokens> ExchangeCodeAsync(
        OAuthClientOptions options,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = options.ClientId,
            ["code_verifier"] = codeVerifier,
        };

        return PostAsync(options, form, previous: null, cancellationToken);
    }

    /// <summary>
    /// Trades a refresh token for a fresh access token.
    /// </summary>
    /// <param name="options">The client settings.</param>
    /// <param name="tokens">The stored tokens, whose refresh token is used.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task<OAuthTokens> RefreshAsync(
        OAuthClientOptions options,
        OAuthTokens tokens,
        CancellationToken cancellationToken = default)
    {
        if (tokens.RefreshToken is not { Length: > 0 } refreshToken)
        {
            throw new AccessTokenUnavailableException(
                "The stored token has expired and the provider issued no refresh token. Run `mcpsense login` again.");
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.ClientId,
        };

        if (options.Scopes.Count > 0)
        {
            form["scope"] = string.Join(' ', options.Scopes);
        }

        return PostAsync(options, form, tokens, cancellationToken);
    }

    private async Task<OAuthTokens> PostAsync(
        OAuthClientOptions options,
        Dictionary<string, string> form,
        OAuthTokens? previous,
        CancellationToken cancellationToken)
    {
        if (options.ClientSecret is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The provider's own error body says which of a dozen things went wrong, so pass it on.
            throw new AccessTokenUnavailableException(
                $"The token endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return Parse(body, previous);
    }

    /// <summary>
    /// Reads a token response.
    /// </summary>
    /// <remarks>
    /// A refresh response often omits the refresh token, which means "keep using the one you have".
    /// Dropping it would silently turn a long-lived login into a single-use one, so the previous
    /// value is carried forward.
    /// </remarks>
    private static OAuthTokens Parse(string body, OAuthTokens? previous)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("access_token", out var accessToken) ||
            accessToken.GetString() is not { Length: > 0 } token)
        {
            throw new AccessTokenUnavailableException("The token endpoint returned no access_token.");
        }

        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_in", out var expiresIn))
        {
            var seconds = expiresIn.ValueKind switch
            {
                JsonValueKind.Number => expiresIn.GetDouble(),
                JsonValueKind.String when double.TryParse(
                    expiresIn.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => (double?)null,
            };

            if (seconds is { } value)
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(value);
            }
        }

        var refreshToken = root.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString()
            : null;

        return new OAuthTokens
        {
            AccessToken = token,
            RefreshToken = string.IsNullOrEmpty(refreshToken) ? previous?.RefreshToken : refreshToken,
            ExpiresAt = expiresAt,
            Scope = root.TryGetProperty("scope", out var scope) ? scope.GetString() : previous?.Scope,
        };
    }
}
