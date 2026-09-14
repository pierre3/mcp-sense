using Cocona;
using McpSense.Server.Authentication;
using McpSense.Core;
using McpSense.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpSense.Tool.Commands;

/// <summary>
/// Serves an OpenAPI spec as an MCP server over stdio.
/// </summary>
public sealed class McpCommand
{
    /// <summary>
    /// Loads the spec, builds the tool catalog and runs the server until the client disconnects.
    /// </summary>
    [Command("mcp", Description = "Serve an OpenAPI spec as an MCP server over stdio.")]
    public async Task<int> RunAsync(
        [Argument(Description = "Path or URL of the OpenAPI spec.")] string spec,
        [Option("base-url", Description = "Base URL of the API. Defaults to the spec's first server entry.")] string? baseUrl = null,
        [Option("bearer-token", Description = "Token sent as `Authorization: Bearer ...` on every request.")] string? bearerToken = null,
        [Option("api-key-header", Description = "Header an API key is sent in, for example X-Api-Key.")] string? apiKeyHeader = null,
        [Option("api-key", Description = "API key value paired with --api-key-header.")] string? apiKey = null,
        [Option("tag", Description = "Only expose operations carrying these tags (comma-separated).")] string? tag = null,
        [Option("mode", Description = "auto (default), direct, or meta.")] string? mode = null,
        [Option("threshold", Description = "Operation count above which auto mode switches to meta-tools. Defaults to 30.")] int threshold = 30,
        [Option("allow", Description = "Only expose operations whose name matches these patterns (comma-separated, * allowed).")] string? allow = null,
        [Option("deny", Description = "Exclude operations whose name matches these patterns (comma-separated, * allowed).")] string? deny = null,
        [Option("header", Description = "Extra header sent on every request, as \"Name: Value\". Repeatable.")] string[]? header = null,
        [Option("ai-model", Description = "Chat model used to rewrite names and descriptions. Off unless set.")] string? aiModel = null,
        [Option("ai-embedding-model", Description = "Embedding model used for semantic search. Search stays lexical unless set.")] string? aiEmbeddingModel = null,
        [Option("ai-endpoint", Description = "OpenAI-compatible endpoint. Defaults to OpenAI.")] string? aiEndpoint = null,
        [Option("ai-api-key", Description = "API key for the AI endpoint. Falls back to OPENAI_API_KEY.")] string? aiApiKey = null,
        [Option("ai-cache", Description = "Directory for cached model responses. Defaults to a per-user location.")] string? aiCache = null,
        [Option("ai-batch-size", Description = "Operations per rewrite request. Defaults to 10.")] int aiBatchSize = 10,
        [Option("ai-keep-names", Description = "Rewrite descriptions but leave operation names as the spec had them.")] bool aiKeepNames = false,
        [Option("oauth-authorization-endpoint", Description = "Use OAuth with tokens stored by `mcpsense login`.")] string? oauthAuthorizationEndpoint = null,
        [Option("oauth-token-endpoint", Description = "URL where refresh tokens are exchanged.")] string? oauthTokenEndpoint = null,
        [Option("oauth-client-id", Description = "Client identifier registered with the provider.")] string? oauthClientId = null,
        [Option("oauth-client-secret", Description = "Client secret, only for providers requiring a confidential client.")] string? oauthClientSecret = null,
        [Option("oauth-scope", Description = "Scopes the stored tokens were granted for, comma or space separated.")] string? oauthScope = null,
        [Option("token-dir", Description = "Where token files are stored. Defaults to a per-user directory.")] string? tokenDir = null,
        CancellationToken cancellationToken = default)
    {
        // Logging is configured before the spec is loaded so that the AI stages, which can take a
        // while on a large spec, can report progress. Everything goes to stderr: stdout is the
        // JSON-RPC stream.
        using var startupLogging = LoggerFactory.Create(logging =>
        {
            logging.AddConsole(consoleOptions => consoleOptions.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        OpenApiSpecLoadResult loaded;
        ToolCatalog catalog;
        Uri baseAddress;

        try
        {
            var groupingOptions = SpecSession.BuildGroupingOptions(mode, threshold, tag, allow, deny);
            var ai = new AiOptions
            {
                ChatModel = aiModel,
                EmbeddingModel = aiEmbeddingModel,
                Endpoint = aiEndpoint,
                ApiKey = aiApiKey,
                CacheDirectory = aiCache,
                BatchSize = aiBatchSize,
                RewriteNames = !aiKeepNames,
            };

            (loaded, catalog) = await SpecSession.LoadCatalogAsync(
                spec, groupingOptions, ai, startupLogging, cancellationToken);
            baseAddress = SpecSession.ResolveBaseAddress(loaded.Document, baseUrl);
        }
        catch (Exception ex) when (ex is OpenApiSpecLoadException or InvalidOperationException)
        {
            // stdout carries the MCP protocol, so diagnostics must go to stderr.
            await Console.Error.WriteLineAsync(ex.Message);
            if (ex is OpenApiSpecLoadException load)
            {
                foreach (var problem in load.Problems)
                {
                    await Console.Error.WriteLineAsync($"  {problem}");
                }
            }

            return 1;
        }

        Dictionary<string, string> headers;
        OAuthCliOptions oauth;
        try
        {
            headers = ParseHeaders(header);
            oauth = new OAuthCliOptions
            {
                AuthorizationEndpoint = oauthAuthorizationEndpoint,
                TokenEndpoint = oauthTokenEndpoint,
                ClientId = oauthClientId,
                ClientSecret = oauthClientSecret,
                Scopes = oauthScope,
                TokenDirectory = tokenDir,
            };

            // Validate now rather than on the first tool call, so a misconfiguration is a startup
            // error the user sees instead of a runtime error the model sees.
            _ = oauth.IsEnabled ? oauth.ToClientOptions() : null;
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var builder = Host.CreateApplicationBuilder();

        // Anything written to stdout would corrupt the JSON-RPC stream, so every log goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        var options = new McpSenseServerOptions
        {
            BaseAddress = baseAddress,
            BearerToken = bearerToken,
            ApiKeyHeaderName = apiKeyHeader,
            ApiKeyValue = apiKey,
            DefaultHeaders = headers,
            AccessTokenProvider = CreateAccessTokenProvider(oauth, startupLogging),
        };

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddHttpClient<ApiDispatcher>();
        builder.Services.AddSingleton<ToolCatalogHandlers>();

        builder.Services
            .AddMcpServer(serverOptions =>
            {
                serverOptions.ServerInfo = new() { Name = "mcpsense", Version = ThisAssembly.Version };
                serverOptions.ServerInstructions = BuildInstructions(loaded, spec, catalog);
            })
            .WithStdioServerTransport()
            .WithListToolsHandler((request, ct) =>
                request.Services!.GetRequiredService<ToolCatalogHandlers>().ListToolsAsync(request, ct))
            .WithCallToolHandler((request, ct) =>
                request.Services!.GetRequiredService<ToolCatalogHandlers>().CallToolAsync(request, ct));

        var host = builder.Build();

        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("McpSense")
            .LogInformation(
                "Serving {ToolCount} tool(s) from {Spec} against {BaseAddress}.",
                catalog.Tools.Count,
                spec,
                baseAddress);

        await host.RunAsync(cancellationToken);
        return 0;
    }

    /// <summary>
    /// Builds the provider that supplies the upstream bearer token, or <c>null</c> to fall back to
    /// the static token.
    /// </summary>
    /// <remarks>
    /// The provider only reads and refreshes what <c>mcpsense login</c> stored. It never starts an
    /// interactive flow: a running server has no way to put a browser in front of anyone.
    /// </remarks>
    private static IAccessTokenProvider? CreateAccessTokenProvider(OAuthCliOptions oauth, ILoggerFactory loggerFactory)
    {
        if (!oauth.IsEnabled)
        {
            return null;
        }

        // A dedicated client: token requests go to the provider, not to the API being proxied.
        var tokenClient = new OAuthTokenClient(new HttpClient());

        return new OAuthAccessTokenProvider(
            oauth.ToClientOptions(),
            oauth.CreateStore(),
            tokenClient,
            loggerFactory.CreateLogger<OAuthAccessTokenProvider>());
    }

    /// <summary>
    /// Parses <c>--header "Name: Value"</c> options, adding a default User-Agent when none was given.
    /// </summary>
    /// <remarks>
    /// The default matters: some APIs (GitHub among them) reject any request without a User-Agent,
    /// and HttpClient sends none of its own.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A value was not in <c>Name: Value</c> form.</exception>
    private static Dictionary<string, string> ParseHeaders(string[]? headers)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in headers ?? [])
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"--header '{entry}' is not in \"Name: Value\" form.");
            }

            parsed[entry[..separator].Trim()] = entry[(separator + 1)..].Trim();
        }

        if (!parsed.ContainsKey("User-Agent"))
        {
            parsed["User-Agent"] = $"mcpsense/{ThisAssembly.Version}";
        }

        return parsed;
    }

    /// <summary>
    /// Builds the instructions the client shows the model before any tool is called.
    /// </summary>
    /// <remarks>
    /// In meta-tool mode this is the only place the model learns the shape of the API up front, so
    /// it carries the group table of contents: which areas exist and how big each is. That is what
    /// lets a model aim a search instead of guessing.
    /// </remarks>
    private static string BuildInstructions(OpenApiSpecLoadResult loaded, string spec, ToolCatalog catalog)
    {
        var title = loaded.Document.Info?.Title ?? spec;

        if (catalog.Mode != ToolCatalogMode.MetaTool)
        {
            return $"Tools generated from the OpenAPI description \"{title}\". "
                + $"{catalog.OperationTools.Count} operation(s) are available, one tool each.";
        }

        var groups = string.Join(
            ", ",
            catalog.Groups.Select(group => $"{group.Tag} ({group.Operations.Count})"));

        return $"Tools generated from the OpenAPI description \"{title}\". "
            + $"It has {catalog.OperationTools.Count} operations — too many to list as individual tools, "
            + $"so they are reached through {MetaToolNames.SearchOperations}, "
            + $"{MetaToolNames.DescribeOperation} and {MetaToolNames.CallOperation}.\n\n"
            + $"Operation groups: {groups}.\n\n"
            + $"Start by searching for what you want to do, then describe the operation to learn its "
            + $"arguments, then call it.";
    }
}

/// <summary>Version information for the running assembly.</summary>
internal static class ThisAssembly
{
    /// <summary>The assembly version, reported to MCP clients as the server version.</summary>
    internal static string Version { get; } =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
