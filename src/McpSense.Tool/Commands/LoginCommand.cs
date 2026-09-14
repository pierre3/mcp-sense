using System.Diagnostics;
using System.Runtime.InteropServices;
using Cocona;
using McpSense.Server.Authentication;

namespace McpSense.Tool.Commands;

/// <summary>
/// Obtains and stores OAuth credentials for an upstream API.
/// </summary>
/// <remarks>
/// This is a separate command because consent needs a browser and a person. The MCP server speaks
/// JSON-RPC over stdin and stdout, so it has no way to show anyone a page or read an answer — it
/// can only use credentials that were obtained beforehand and refresh them silently afterwards.
/// </remarks>
public sealed class LoginCommand
{
    /// <summary>Runs the authorization code flow and stores the resulting tokens.</summary>
    [Command("login", Description = "Authorize McpSense against an API using OAuth 2.0 and store the tokens.")]
    public async Task<int> RunAsync(
        [Option("oauth-authorization-endpoint", Description = "URL where the user approves access.")] string authorizationEndpoint,
        [Option("oauth-token-endpoint", Description = "URL where codes and refresh tokens are exchanged.")] string tokenEndpoint,
        [Option("oauth-client-id", Description = "Client identifier registered with the provider.")] string clientId,
        [Option("oauth-client-secret", Description = "Client secret, only for providers requiring a confidential client.")] string? clientSecret = null,
        [Option("oauth-scope", Description = "Scopes to request, comma or space separated.")] string? scope = null,
        [Option("oauth-redirect-port", Description = "Fixed loopback port to redirect to. Defaults to any free port.")] int redirectPort = 0,
        [Option("token-dir", Description = "Where token files are stored. Defaults to a per-user directory.")] string? tokenDir = null,
        [Option("no-browser", Description = "Print the authorization URL instead of opening a browser.")] bool noBrowser = false,
        CancellationToken cancellationToken = default)
    {
        var cli = new OAuthCliOptions
        {
            AuthorizationEndpoint = authorizationEndpoint,
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Scopes = scope,
            RedirectPort = redirectPort,
            TokenDirectory = tokenDir,
        };

        OAuthClientOptions options;
        try
        {
            options = cli.ToClientOptions();
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        using var httpClient = new HttpClient();
        var flow = new AuthorizationCodeFlow(new OAuthTokenClient(httpClient));

        try
        {
            var tokens = await flow.AuthorizeAsync(
                options,
                url =>
                {
                    Console.WriteLine("Open this URL to authorize McpSense:");
                    Console.WriteLine();
                    Console.WriteLine(url);
                    Console.WriteLine();

                    if (!noBrowser)
                    {
                        OpenBrowser(url);
                    }

                    Console.WriteLine("Waiting for the authorization to complete...");
                },
                cancellationToken);

            await cli.CreateStore().SaveAsync(options.StorageKey, tokens, cancellationToken);

            Console.WriteLine("Authorized. Tokens stored.");
            if (tokens.RefreshToken is null)
            {
                // Worth saying plainly: without one, the login lasts only as long as this token.
                Console.WriteLine(
                    "Note: the provider issued no refresh token, so you will have to run `mcpsense login` again when this one expires.");
            }
            else if (tokens.ExpiresAt is { } expiry)
            {
                Console.WriteLine($"The access token expires at {expiry:u} and will be refreshed automatically.");
            }

            return 0;
        }
        catch (AccessTokenUnavailableException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    /// <summary>Launches the platform's default browser.</summary>
    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // UseShellExecute is what makes Windows resolve the default browser for a URL.
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Headless machines have no browser to launch; the URL was printed above regardless.
            Console.WriteLine("(Could not launch a browser automatically. Open the URL above manually.)");
        }
    }
}
