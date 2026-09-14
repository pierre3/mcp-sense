using System.ClientModel;
using McpSense.Ai;
using McpSense.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace McpSense.Tool;

/// <summary>
/// The AI settings shared by the CLI commands, and the wiring that turns them into clients.
/// </summary>
/// <remarks>
/// Everything here is opt-in. With no model named, no client is built, nothing is sent anywhere and
/// McpSense behaves exactly as it does without the AI packages present.
/// </remarks>
internal sealed class AiOptions
{
    /// <summary>The chat model used to rewrite names and descriptions. AI stays off when null.</summary>
    internal string? ChatModel { get; init; }

    /// <summary>The embedding model used for semantic search. Search stays lexical when null.</summary>
    internal string? EmbeddingModel { get; init; }

    /// <summary>An OpenAI-compatible endpoint. Defaults to OpenAI itself.</summary>
    internal string? Endpoint { get; init; }

    /// <summary>The API key. Falls back to the OPENAI_API_KEY environment variable.</summary>
    internal string? ApiKey { get; init; }

    /// <summary>Where cached model responses live.</summary>
    internal string? CacheDirectory { get; init; }

    /// <summary>How many operations go into one rewrite request.</summary>
    internal int BatchSize { get; init; } = 10;

    /// <summary>Whether the model may rename operations as well as redescribe them.</summary>
    internal bool RewriteNames { get; init; } = true;

    /// <summary>Whether anything AI-backed was asked for at all.</summary>
    internal bool IsEnabled => ChatModel is not null || EmbeddingModel is not null;

    /// <summary>
    /// Applies the AI stages to a set of operations: rewriting them when a chat model is
    /// configured, and returning a search factory that uses embeddings when one is configured.
    /// </summary>
    /// <returns>
    /// The operations to build the catalog from, and the search factory to build it with
    /// (<c>null</c> to keep the default lexical search).
    /// </returns>
    internal async Task<(IReadOnlyList<OperationDescriptor> Operations, OperationSearchFactory? SearchFactory)> ApplyAsync(
        IReadOnlyList<OperationDescriptor> operations,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return (operations, null);
        }

        var client = CreateOpenAIClient();
        var cache = new FileDistributedCache(ResolveCacheDirectory());

        if (ChatModel is { Length: > 0 } chatModel)
        {
            // The cache wraps the client, so an unchanged spec costs nothing on the second start.
            var chatClient = new ChatClientBuilder(client.GetChatClient(chatModel).AsIChatClient())
                .UseDistributedCache(cache)
                .Build();

            var enhancer = new DescriptionEnhancer(
                chatClient,
                new DescriptionEnhancerOptions { BatchSize = BatchSize, RewriteNames = RewriteNames },
                loggerFactory.CreateLogger<DescriptionEnhancer>());

            operations = await enhancer.EnhanceAsync(operations, cancellationToken);
        }

        OperationSearchFactory? searchFactory = null;
        if (EmbeddingModel is { Length: > 0 } embeddingModel)
        {
            var generator = client.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator();
            searchFactory = async (indexed, ct) =>
                await EmbeddingOperationSearch.CreateAsync(generator, indexed, ct);
        }

        return (operations, searchFactory);
    }

    private OpenAIClient CreateOpenAIClient()
    {
        var apiKey = ApiKey
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "No API key. Pass --ai-api-key or set the OPENAI_API_KEY environment variable.");

        var options = new OpenAIClientOptions();
        if (Endpoint is { Length: > 0 } endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed))
            {
                throw new InvalidOperationException($"--ai-endpoint '{endpoint}' is not an absolute URL.");
            }

            options.Endpoint = parsed;
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    /// <summary>
    /// Resolves the cache directory, defaulting to a per-user location so the cache survives
    /// between runs without cluttering the working directory.
    /// </summary>
    private string ResolveCacheDirectory()
        => CacheDirectory is { Length: > 0 } directory
            ? directory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "mcpsense",
                "ai-cache");
}
