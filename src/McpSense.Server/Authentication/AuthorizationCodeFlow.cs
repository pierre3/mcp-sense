using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace McpSense.Server.Authentication;

/// <summary>
/// Runs the OAuth 2.0 authorization code flow with PKCE against a loopback redirect.
/// </summary>
/// <remarks>
/// <para>
/// The loopback redirect is what lets a desktop tool complete the flow without a hosted callback
/// URL: the provider redirects the browser back to <c>http://127.0.0.1:port</c>, where this class
/// is listening just long enough to catch the code.
/// </para>
/// <para>
/// PKCE is mandatory here rather than optional. McpSense is a public client with no usable secret,
/// so without the proof key an authorization code intercepted on the loopback interface could be
/// redeemed by anything else running on the machine.
/// </para>
/// </remarks>
public sealed class AuthorizationCodeFlow
{
    private readonly OAuthTokenClient _tokenClient;

    /// <summary>Creates a flow that redeems codes through the given client.</summary>
    public AuthorizationCodeFlow(OAuthTokenClient tokenClient)
        => _tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));

    /// <summary>
    /// Runs the flow end to end and returns the granted tokens.
    /// </summary>
    /// <param name="options">The OAuth client settings.</param>
    /// <param name="openBrowser">
    /// Shows the authorization URL to the user, normally by launching their browser.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="AccessTokenUnavailableException">The user denied access, or the provider reported an error.</exception>
    public async Task<OAuthTokens> AuthorizeAsync(
        OAuthClientOptions options,
        Action<string> openBrowser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openBrowser);

        var port = options.RedirectPort > 0 ? options.RedirectPort : FindFreePort();
        var redirectUri = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";

        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);
        var state = CreateRandomString(32);

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        try
        {
            openBrowser(BuildAuthorizationUrl(options, redirectUri, challenge, state));

            var code = await WaitForCodeAsync(listener, state, cancellationToken).ConfigureAwait(false);

            return await _tokenClient
                .ExchangeCodeAsync(options, code, verifier, redirectUri, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string BuildAuthorizationUrl(
        OAuthClientOptions options,
        string redirectUri,
        string codeChallenge,
        string state)
    {
        var query = HttpUtility.ParseQueryString(options.AuthorizationEndpoint.Query);
        query["response_type"] = "code";
        query["client_id"] = options.ClientId;
        query["redirect_uri"] = redirectUri;
        query["state"] = state;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";

        if (options.Scopes.Count > 0)
        {
            query["scope"] = string.Join(' ', options.Scopes);
        }

        var builder = new UriBuilder(options.AuthorizationEndpoint) { Query = query.ToString() };
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Waits for the browser to come back with a code, answering each request so the user sees
    /// something other than a hung tab.
    /// </summary>
    private static async Task<string> WaitForCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);

            var error = query["error"];
            var code = query["code"];
            var state = query["state"];

            if (error is null && code is null)
            {
                // Browsers ask for /favicon.ico and the like; ignore anything that is not the callback.
                await RespondAsync(context, 404, "Not found.").ConfigureAwait(false);
                continue;
            }

            if (error is not null)
            {
                var description = query["error_description"];
                await RespondAsync(context, 400, $"Authorization failed: {error}").ConfigureAwait(false);
                throw new AccessTokenUnavailableException(
                    $"The provider refused authorization: {error}{(description is null ? "" : $" - {description}")}");
            }

            // The state check is what stops another site from walking a user's browser through this
            // flow and planting someone else's authorization code here.
            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await RespondAsync(context, 400, "Authorization failed: state mismatch.").ConfigureAwait(false);
                throw new AccessTokenUnavailableException(
                    "The authorization response carried the wrong state value and was rejected.");
            }

            await RespondAsync(context, 200, "McpSense is authorized. You can close this tab.").ConfigureAwait(false);
            return code!;
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, int statusCode, string message)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=\"utf-8\"><title>McpSense</title><p>{WebUtility.HtmlEncode(message)}</p>");

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>Creates the high-entropy secret that PKCE binds the authorization code to.</summary>
    internal static string CreateCodeVerifier() => CreateRandomString(64);

    /// <summary>Hashes the verifier the way the S256 challenge method requires.</summary>
    internal static string CreateCodeChallenge(string codeVerifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string CreateRandomString(int byteCount)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    /// <summary>base64url without padding, as OAuth requires.</summary>
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Asks the OS for an unused loopback port by binding one and letting it go.</summary>
    private static int FindFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}
