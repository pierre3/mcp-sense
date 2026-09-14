using System.Net;
using System.Security.Cryptography;
using System.Text;
using McpSense.Server.Authentication;
using Xunit;

namespace McpSense.Server.Tests;

/// <summary>
/// Covers the upstream OAuth client: PKCE, refreshing an expiring token, and what happens when
/// nobody has logged in yet.
/// </summary>
public class OAuthTests
{
    private static OAuthClientOptions Options(params string[] scopes) => new()
    {
        AuthorizationEndpoint = new Uri("https://provider.test/authorize"),
        TokenEndpoint = new Uri("https://provider.test/token"),
        ClientId = "client-1",
        Scopes = scopes,
    };

    [Fact]
    public void StorageKeySeparatesDifferentClientsAndScopes()
    {
        // Sharing a stored token across scopes would silently hand an operation broader or
        // narrower access than it was granted.
        Assert.NotEqual(Options("read").StorageKey, Options("read", "write").StorageKey);
        Assert.NotEqual(
            Options("read").StorageKey,
            (Options("read") with { ClientId = "client-2" }).StorageKey);
        Assert.Equal(Options("read").StorageKey, Options("read").StorageKey);
    }

    [Fact]
    public void CodeChallengeIsTheBase64UrlSha256OfTheVerifier()
    {
        var verifier = AuthorizationCodeFlow.CreateCodeVerifier();

        var challenge = AuthorizationCodeFlow.CreateCodeChallenge(verifier);

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(expected, challenge);
        // base64url must not carry characters that would need escaping in a query string.
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
        Assert.DoesNotContain('=', challenge);
    }

    [Fact]
    public void EachVerifierIsDifferent()
    {
        var verifiers = Enumerable.Range(0, 20).Select(_ => AuthorizationCodeFlow.CreateCodeVerifier()).ToList();

        Assert.Equal(verifiers.Count, verifiers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TokensAreConsideredExpiredWithinTheRefreshMargin()
    {
        var tokens = new OAuthTokens
        {
            AccessToken = "a",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };

        Assert.True(tokens.IsExpired(TimeSpan.FromSeconds(60)));
        Assert.False(tokens.IsExpired(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TokensWithoutAnExpiryAreNeverConsideredExpired()
    {
        var tokens = new OAuthTokens { AccessToken = "a" };

        Assert.False(tokens.IsExpired(TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task ReturnsTheStoredTokenWhileItIsStillValid()
    {
        var store = new InMemoryTokenStore();
        var options = Options("read");
        await store.SaveAsync(options.StorageKey, new OAuthTokens
        {
            AccessToken = "still-good",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });

        var handler = new CountingTokenEndpoint();
        var provider = new OAuthAccessTokenProvider(options, store, new OAuthTokenClient(new HttpClient(handler)));

        Assert.Equal("still-good", await provider.GetAccessTokenAsync());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RefreshesAnExpiringTokenAndStoresTheResult()
    {
        var store = new InMemoryTokenStore();
        var options = Options("read");
        await store.SaveAsync(options.StorageKey, new OAuthTokens
        {
            AccessToken = "stale",
            RefreshToken = "refresh-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5),
        });

        var handler = new CountingTokenEndpoint("""{"access_token":"fresh","expires_in":3600}""");
        var provider = new OAuthAccessTokenProvider(options, store, new OAuthTokenClient(new HttpClient(handler)));

        Assert.Equal("fresh", await provider.GetAccessTokenAsync());
        Assert.Equal(1, handler.CallCount);

        var stored = await store.LoadAsync(options.StorageKey);
        Assert.Equal("fresh", stored!.AccessToken);
        // The response carried no refresh token, which means "keep the one you have".
        Assert.Equal("refresh-1", stored.RefreshToken);
    }

    [Fact]
    public async Task RefreshesOnlyOnceWhenSeveralCallsArriveTogether()
    {
        // Providers that rotate refresh tokens invalidate the login if the same one is redeemed
        // twice, so concurrent tool calls must not each start a refresh.
        var store = new InMemoryTokenStore();
        var options = Options("read");
        await store.SaveAsync(options.StorageKey, new OAuthTokens
        {
            AccessToken = "stale",
            RefreshToken = "refresh-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        });

        var handler = new CountingTokenEndpoint("""{"access_token":"fresh","expires_in":3600}""", delay: TimeSpan.FromMilliseconds(50));
        var provider = new OAuthAccessTokenProvider(options, store, new OAuthTokenClient(new HttpClient(handler)));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetAccessTokenAsync().AsTask()));

        Assert.All(results, token => Assert.Equal("fresh", token));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SaysHowToLogInWhenNothingIsStored()
    {
        var options = Options("read");
        var provider = new OAuthAccessTokenProvider(
            options,
            new InMemoryTokenStore(),
            new OAuthTokenClient(new HttpClient(new CountingTokenEndpoint())));

        var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(
            async () => await provider.GetAccessTokenAsync());

        Assert.Contains("mcpsense login", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaysHowToLogInWhenTheTokenExpiredWithNoRefreshToken()
    {
        var store = new InMemoryTokenStore();
        var options = Options("read");
        await store.SaveAsync(options.StorageKey, new OAuthTokens
        {
            AccessToken = "stale",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        });

        var provider = new OAuthAccessTokenProvider(
            options,
            store,
            new OAuthTokenClient(new HttpClient(new CountingTokenEndpoint())));

        var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(
            async () => await provider.GetAccessTokenAsync());

        Assert.Contains("mcpsense login", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassesTheProvidersErrorBodyThrough()
    {
        var store = new InMemoryTokenStore();
        var options = Options("read");
        await store.SaveAsync(options.StorageKey, new OAuthTokens
        {
            AccessToken = "stale",
            RefreshToken = "revoked",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        });

        var handler = new CountingTokenEndpoint("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var provider = new OAuthAccessTokenProvider(options, store, new OAuthTokenClient(new HttpClient(handler)));

        var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(
            async () => await provider.GetAccessTokenAsync());

        Assert.Contains("invalid_grant", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendsTheRefreshGrantWithTheClientIdAndScopes()
    {
        var store = new InMemoryTokenStore();
        var options = Options("read", "write");
        await store.SaveAsync(options.StorageKey, new OAuthTokens
        {
            AccessToken = "stale",
            RefreshToken = "refresh-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        });

        var handler = new CountingTokenEndpoint("""{"access_token":"fresh","expires_in":3600}""");
        var provider = new OAuthAccessTokenProvider(options, store, new OAuthTokenClient(new HttpClient(handler)));

        await provider.GetAccessTokenAsync();

        Assert.Contains("grant_type=refresh_token", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("client_id=client-1", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("read+write", handler.LastBody, StringComparison.Ordinal);
    }

    private sealed class InMemoryTokenStore : ITokenStore
    {
        private readonly Dictionary<string, OAuthTokens> _entries = new(StringComparer.Ordinal);

        public ValueTask<OAuthTokens?> LoadAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_entries.GetValueOrDefault(key));

        public ValueTask SaveAsync(string key, OAuthTokens tokens, CancellationToken cancellationToken = default)
        {
            _entries[key] = tokens;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingTokenEndpoint(
        string body = """{"access_token":"x"}""",
        HttpStatusCode statusCode = HttpStatusCode.OK,
        TimeSpan? delay = null) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
        }
    }
}
