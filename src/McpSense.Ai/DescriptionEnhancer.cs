using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using McpSense.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpSense.Ai;

/// <summary>
/// Rewrites operation names and descriptions for a model to read, using any
/// <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Specs are written for developers who already have the documentation open. Summaries are often a
/// terse fragment, descriptions assume surrounding context, and names are shaped by whatever code
/// generator the vendor uses. A model choosing between tools has none of that context, so this
/// stage restates each operation in terms of what it does and when to reach for it.
/// </para>
/// <para>
/// This is opt-in, and failure is never fatal. If the model errors, times out or returns something
/// unparseable, the operation keeps its original text: an AI outage must not stop the server from
/// starting, because McpSense is required to work fully without AI in the first place.
/// </para>
/// <para>
/// Caching belongs outside this class. Wrap the <see cref="IChatClient"/> with
/// <c>ChatClientBuilder.UseDistributedCache</c> so that repeated startups over an unchanged spec
/// cost nothing.
/// </para>
/// </remarks>
public sealed class DescriptionEnhancer
{
    private const string SystemPrompt = """
        You rewrite REST API operation descriptions so that an AI agent can choose and call the right
        one. You are given a JSON array of operations taken from an OpenAPI description.

        For each operation, return:
          - "name": a short lower_snake_case name saying what the operation does, at most 48
            characters. Keep the given name if it is already clear.
          - "description": one or two sentences. Say what the operation does and when to use it.
            Mention anything a caller would get wrong. Do not restate the HTTP method or path, and
            do not invent behaviour the input does not support.
          - "parameters": an object mapping each given parameter name to a short description of what
            to pass. Include only parameters that were given. Omit the key entirely if there are none.

        Reply with a JSON array of objects, each having "id", "name", "description" and optionally
        "parameters". "id" must be copied verbatim from the input. Return nothing but the JSON.
        """;

    private readonly IChatClient _chatClient;
    private readonly DescriptionEnhancerOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates an enhancer.</summary>
    /// <param name="chatClient">The model to use. Wrap it in a caching client to avoid repeat cost.</param>
    /// <param name="options">Batching and naming behaviour.</param>
    /// <param name="logger">Receives a report of what was rewritten and what failed.</param>
    public DescriptionEnhancer(
        IChatClient chatClient,
        DescriptionEnhancerOptions? options = null,
        ILogger<DescriptionEnhancer>? logger = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? new DescriptionEnhancerOptions();
        _logger = logger ?? NullLogger<DescriptionEnhancer>.Instance;
    }

    /// <summary>
    /// Rewrites the given operations, returning them in their original order.
    /// </summary>
    /// <param name="operations">The operations to rewrite.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Rewritten operations. Any operation the model did not cover, or covered unusably, is
    /// returned exactly as it came in.
    /// </returns>
    public async Task<IReadOnlyList<OperationDescriptor>> EnhanceAsync(
        IReadOnlyList<OperationDescriptor> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
        {
            return operations;
        }

        var batches = operations
            .Select(static (operation, index) => (operation, index))
            .GroupBy(entry => entry.index / Math.Max(1, _options.BatchSize))
            .Select(static group => group.Select(static entry => entry.operation).ToList())
            .ToList();

        _logger.LogInformation(
            "Rewriting {OperationCount} operation(s) in {BatchCount} model request(s).",
            operations.Count,
            batches.Count);

        var rewrites = new Dictionary<string, Rewrite>(operations.Count, StringComparer.Ordinal);
        using var concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        var results = await Task.WhenAll(batches.Select(async batch =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RewriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);

        foreach (var entry in results.SelectMany(static result => result))
        {
            rewrites[entry.Key] = entry.Value;
        }

        var enhanced = Apply(operations, rewrites);

        _logger.LogInformation(
            "Rewrote {RewrittenCount} of {OperationCount} operation(s).",
            rewrites.Count,
            operations.Count);

        return enhanced;
    }

    private async Task<IReadOnlyDictionary<string, Rewrite>> RewriteBatchAsync(
        IReadOnlyList<OperationDescriptor> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new JsonArray([.. batch.Select(Describe)]);

            var response = await _chatClient.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, SystemPrompt),
                        new ChatMessage(ChatRole.User, request.ToJsonString()),
                    ],
                    new ChatOptions { Temperature = 0f },
                    cancellationToken)
                .ConfigureAwait(false);

            return Parse(response.Text, batch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keeping the spec's own wording is always a valid outcome, so a failed batch is worth
            // a warning but not worth failing the run over.
            _logger.LogWarning(
                ex,
                "Could not rewrite a batch of {Count} operation(s); keeping the spec's own text.",
                batch.Count);
            return new Dictionary<string, Rewrite>(StringComparer.Ordinal);
        }
    }

    /// <summary>Renders one operation as the input the model is asked to rewrite.</summary>
    private static JsonObject Describe(OperationDescriptor operation)
    {
        var entry = new JsonObject
        {
            ["id"] = operation.Name,
            ["method"] = operation.Method.Method,
            ["path"] = operation.PathTemplate,
        };

        if (operation.Summary is not null)
        {
            entry["summary"] = operation.Summary;
        }

        if (operation.Description is not null && operation.Description != operation.Summary)
        {
            entry["description"] = operation.Description;
        }

        if (operation.Tags.Count > 0)
        {
            entry["tags"] = new JsonArray([.. operation.Tags.Select(static tag => (JsonNode?)JsonValue.Create(tag))]);
        }

        if (operation.Parameters.Count > 0)
        {
            var parameters = new JsonObject();
            foreach (var parameter in operation.Parameters)
            {
                parameters[parameter.Name] = parameter.Description ?? string.Empty;
            }

            entry["parameters"] = parameters;
        }

        return entry;
    }

    /// <summary>
    /// Reads the model's reply. Anything malformed is dropped rather than thrown, so one bad entry
    /// does not cost the whole batch.
    /// </summary>
    private IReadOnlyDictionary<string, Rewrite> Parse(string? text, IReadOnlyList<OperationDescriptor> batch)
    {
        var rewrites = new Dictionary<string, Rewrite>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return rewrites;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(StripCodeFence(text));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The model's reply was not valid JSON; keeping the spec's own text.");
            return rewrites;
        }

        if (parsed is not JsonArray entries)
        {
            return rewrites;
        }

        var known = batch.ToDictionary(static operation => operation.Name, StringComparer.Ordinal);

        foreach (var entry in entries.OfType<JsonObject>())
        {
            var id = entry["id"]?.GetValue<string>();
            if (id is null || !known.TryGetValue(id, out var operation))
            {
                continue;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entry["parameters"] is JsonObject parameterEntries)
            {
                var parameterNames = operation.Parameters
                    .Select(static parameter => parameter.Name)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var parameter in parameterEntries)
                {
                    // Ignore parameters the model invented: they would describe something the
                    // request never carries.
                    if (parameterNames.Contains(parameter.Key) &&
                        parameter.Value?.GetValue<string>() is { Length: > 0 } description)
                    {
                        parameters[parameter.Key] = description;
                    }
                }
            }

            rewrites[id] = new Rewrite
            {
                Name = _options.RewriteNames ? NullIfBlank(entry["name"]?.GetValue<string>()) : null,
                Description = NullIfBlank(entry["description"]?.GetValue<string>()),
                Parameters = parameters,
            };
        }

        return rewrites;
    }

    /// <summary>
    /// Applies the rewrites, keeping names unique. A model asked to name things independently will
    /// sometimes land on the same name twice, and the catalog is keyed by name.
    /// </summary>
    private static IReadOnlyList<OperationDescriptor> Apply(
        IReadOnlyList<OperationDescriptor> operations,
        IReadOnlyDictionary<string, Rewrite> rewrites)
    {
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<OperationDescriptor>(operations.Count);

        foreach (var operation in operations)
        {
            if (!rewrites.TryGetValue(operation.Name, out var rewrite))
            {
                result.Add(operation with { Name = EnsureUnique(operation.Name, usedNames) });
                continue;
            }

            var name = rewrite.Name is null
                ? operation.Name
                : Sanitize(rewrite.Name);

            result.Add(operation with
            {
                Name = EnsureUnique(name, usedNames),
                Description = rewrite.Description ?? operation.Description,
                Parameters = ApplyParameters(operation.Parameters, rewrite.Parameters),
            });
        }

        return result;
    }

    private static IReadOnlyList<OperationParameter> ApplyParameters(
        IReadOnlyList<OperationParameter> parameters,
        IReadOnlyDictionary<string, string> rewrites)
    {
        if (rewrites.Count == 0)
        {
            return parameters;
        }

        return parameters
            .Select(parameter => rewrites.TryGetValue(parameter.Name, out var description)
                ? parameter with { Description = description }
                : parameter)
            .ToList();
    }

    /// <summary>Strips a ```json fence, which models add even when told not to.</summary>
    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing < 0 ? body : body[..closing]).Trim();
    }

    /// <summary>Normalises to characters valid in an MCP tool name.</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var sanitized = builder.ToString().Trim('_');
        return sanitized.Length == 0 ? "operation" : sanitized;
    }

    private static string EnsureUnique(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{name}_{suffix}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class Rewrite
    {
        public required string? Name { get; init; }

        public required string? Description { get; init; }

        public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    }
}
