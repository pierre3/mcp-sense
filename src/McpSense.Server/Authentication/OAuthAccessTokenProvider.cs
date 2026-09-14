using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpSense.Server.Authentication;

/// <summary>
/// Supplies an OAuth access token, refreshing it silently when it is about to expire.
/// </summary>
/// <remarks>
/// <para>
/// This provider never opens a browser. An MCP server speaking stdio has no way to interact with a
/// person — its stdin and stdout are the protocol — so obtaining consent is a separate, earlier
/// step (<c>mcpsense login</c>). If there is nothing stored, the tool call fails with a message
/// saying so rather than hanging on a prompt nobody will see.
/// </para>
/// <para>
/// A refresh is serialised: several tool calls can run at once, and letting each of them redeem the
/// same refresh token races, wastes requests, and with providers that rotate refresh tokens can
/// invalidate the login outright.
/// </para>
/// </remarks>
public sealed class OAuthAccessTokenProvider : IAccessTokenProvider
{
    /// <summary>How long before expiry a token is treated as spent.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly OAuthClientOptions _options;
    private readonly ITokenStore _store;
    private readonly OAuthTokenClient _tokenClient;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private OAuthTokens? _cached;

    /// <summary>Creates a provider.</summary>
    /// <param name="options">The OAuth client settings.</param>
    /// <param name="store">Where tokens are persisted between runs.</param>
    /// <param name="tokenClient">Used to redeem refresh tokens.</param>
    /// <param name="logger">Receives a note whenever a token is refreshed.</param>
    public OAuthAccessTokenProvider(
        OAuthClientOptions options,
        ITokenStore store,
        OAuthTokenClient tokenClient,
        ILogger<OAuthAccessTokenProvider>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));
        _logger = logger ?? NullLogger<OAuthAccessTokenProvider>.Instance;
    }

    /// <inheritdoc />
    /// <exception cref="AccessTokenUnavailableException">
    /// Nothing is stored, or the stored token expired and could not be refreshed.
    /// </exception>
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached && !cached.IsExpired(RefreshMargin))
        {
            return cached.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited for the lock.
            if (_cached is { } current && !current.IsExpired(RefreshMargin))
            {
                return current.AccessToken;
            }

            var stored = _cached ?? await _store.LoadAsync(_options.StorageKey, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                throw new AccessTokenUnavailableException(
                    "No stored credentials for this API. Run `mcpsense login` to authorize McpSense first.");
            }

            if (!stored.IsExpired(RefreshMargin))
            {
                _cached = stored;
                return stored.AccessToken;
            }

            _logger.LogInformation("The access token expired; refreshing it.");
            var refreshed = await _tokenClient.RefreshAsync(_options, stored, cancellationToken).ConfigureAwait(false);

            await _store.SaveAsync(_options.StorageKey, refreshed, cancellationToken).ConfigureAwait(false);
            _cached = refreshed;
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
