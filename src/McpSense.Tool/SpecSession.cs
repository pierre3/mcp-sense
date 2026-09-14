using McpSense.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi;

namespace McpSense.Tool;

/// <summary>
/// Loads a spec and prepares the tool catalog, sharing that work between the CLI commands.
/// </summary>
internal static class SpecSession
{
    /// <summary>
    /// Reads a spec, models its operations and builds the tool catalog.
    /// </summary>
    /// <param name="spec">Path or URL of the spec.</param>
    /// <param name="options">Filtering and presentation options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal static async Task<(OpenApiSpecLoadResult Loaded, ToolCatalog Catalog)> LoadCatalogAsync(
        string spec,
        ToolGroupingOptions options,
        CancellationToken cancellationToken)
        => await LoadCatalogAsync(spec, options, ai: null, loggerFactory: null, cancellationToken);

    /// <summary>
    /// Reads a spec, models its operations, optionally runs the AI stages, and builds the catalog.
    /// </summary>
    /// <param name="spec">Path or URL of the spec.</param>
    /// <param name="options">Filtering and presentation options.</param>
    /// <param name="ai">AI settings, or <c>null</c> to skip every AI stage.</param>
    /// <param name="loggerFactory">Used by the AI stages to report what they did.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal static async Task<(OpenApiSpecLoadResult Loaded, ToolCatalog Catalog)> LoadCatalogAsync(
        string spec,
        ToolGroupingOptions options,
        AiOptions? ai,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        var loaded = await OpenApiSpecLoader.LoadAsync(spec, cancellationToken);
        IReadOnlyList<OperationDescriptor> operations = new OperationModelBuilder().Build(loaded.Document);

        OperationSearchFactory? searchFactory = null;
        if (ai is { IsEnabled: true })
        {
            // The AI stages run before grouping so that a rewritten name and description feed both
            // the tool catalog and the search index.
            (operations, searchFactory) = await ai.ApplyAsync(
                operations,
                loggerFactory ?? NullLoggerFactory.Instance,
                cancellationToken);
        }

        var catalog = await new ToolCatalogBuilder()
            .BuildAsync(operations, options, searchFactory, cancellationToken);

        return (loaded, catalog);
    }

    /// <summary>
    /// Assembles grouping options from the options every command shares.
    /// </summary>
    internal static ToolGroupingOptions BuildGroupingOptions(
        string? mode,
        int threshold,
        string? tag,
        string? allow,
        string? deny)
        => new()
        {
            Mode = ParseMode(mode),
            MetaToolThreshold = threshold,
            AllowedTags = Split(tag),
            AllowedOperations = Split(allow),
            DeniedOperations = Split(deny),
        };

    private static ToolCatalogMode ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "auto" => ToolCatalogMode.Auto,
        "direct" => ToolCatalogMode.Direct,
        "meta" or "metatool" or "meta-tool" => ToolCatalogMode.MetaTool,
        _ => throw new InvalidOperationException($"--mode '{mode}' is not one of: auto, direct, meta."),
    };

    /// <summary>Splits a comma-separated option value, dropping blanks.</summary>
    private static IReadOnlyList<string>? Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return parts.Count == 0 ? null : parts;
    }

    /// <summary>
    /// Works out the base address requests are resolved against: an explicit override when given,
    /// otherwise the spec's first absolute <c>servers</c> entry.
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither source yielded an absolute URL.</exception>
    internal static Uri ResolveBaseAddress(OpenApiDocument document, string? baseUrlOverride)
    {
        if (baseUrlOverride is { Length: > 0 })
        {
            if (!Uri.TryCreate(baseUrlOverride, UriKind.Absolute, out var overridden))
            {
                throw new InvalidOperationException($"--base-url '{baseUrlOverride}' is not an absolute URL.");
            }

            return overridden;
        }

        foreach (var server in document.Servers ?? [])
        {
            if (Uri.TryCreate(server?.Url, UriKind.Absolute, out var fromSpec))
            {
                return fromSpec;
            }
        }

        throw new InvalidOperationException(
            "The spec declares no absolute server URL. Pass --base-url to say where the API lives.");
    }
}
