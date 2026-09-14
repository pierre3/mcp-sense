using System.Text.Json;
using Cocona;
using McpSense.Core;
using Microsoft.Extensions.Logging;

namespace McpSense.Tool.Commands;

/// <summary>
/// Debug command that prints the tools McpSense would advertise, including their input schemas.
/// </summary>
/// <remarks>
/// This is the counterpart to <see cref="DumpOperationsCommand"/> one stage further down the
/// pipeline: it shows what an MCP client actually receives, which is what matters when a model
/// calls a tool with the wrong shape of arguments.
/// </remarks>
public sealed class DumpToolsCommand
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new() { WriteIndented = true };

    /// <summary>Builds the tool catalog for a spec and prints it.</summary>
    [Command("dump-tools", Description = "Print the MCP tools McpSense generates from an OpenAPI spec.")]
    public async Task<int> RunAsync(
        [Argument(Description = "Path or URL of the OpenAPI spec.")] string spec,
        [Option("schema", Description = "Print each tool's full input schema.")] bool schema = false,
        [Option("tool", Description = "Only show the tool with this name.")] string? tool = null,
        [Option("mode", Description = "auto (default), direct, or meta.")] string? mode = null,
        [Option("threshold", Description = "Operation count above which auto mode switches to meta-tools. Defaults to 30.")] int threshold = 30,
        [Option("tag", Description = "Only include operations carrying these tags (comma-separated).")] string? tag = null,
        [Option("allow", Description = "Only include operations whose name matches these patterns (comma-separated, * allowed).")] string? allow = null,
        [Option("deny", Description = "Exclude operations whose name matches these patterns (comma-separated, * allowed).")] string? deny = null,
        [Option("ai-model", Description = "Chat model used to rewrite names and descriptions. Off unless set.")] string? aiModel = null,
        [Option("ai-endpoint", Description = "OpenAI-compatible endpoint. Defaults to OpenAI.")] string? aiEndpoint = null,
        [Option("ai-api-key", Description = "API key for the AI endpoint. Falls back to OPENAI_API_KEY.")] string? aiApiKey = null,
        [Option("ai-cache", Description = "Directory for cached model responses. Defaults to a per-user location.")] string? aiCache = null,
        [Option("ai-batch-size", Description = "Operations per rewrite request. Defaults to 10.")] int aiBatchSize = 10,
        [Option("ai-keep-names", Description = "Rewrite descriptions but leave operation names as the spec had them.")] bool aiKeepNames = false,
        CancellationToken cancellationToken = default)
    {
        // This command prints to stdout, so its logs go to stderr to keep the two separable.
        using var logging = LoggerFactory.Create(builder =>
            builder.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));

        ToolCatalog catalog;
        try
        {
            var options = SpecSession.BuildGroupingOptions(mode, threshold, tag, allow, deny);
            var ai = new AiOptions
            {
                ChatModel = aiModel,
                Endpoint = aiEndpoint,
                ApiKey = aiApiKey,
                CacheDirectory = aiCache,
                BatchSize = aiBatchSize,
                RewriteNames = !aiKeepNames,
            };

            (_, catalog) = await SpecSession.LoadCatalogAsync(spec, options, ai, logging, cancellationToken);
        }
        catch (Exception ex) when (ex is OpenApiSpecLoadException or InvalidOperationException)
        {
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

        Console.WriteLine($"mode:       {catalog.Mode}");
        Console.WriteLine($"operations: {catalog.OperationTools.Count}");
        Console.WriteLine($"advertised: {catalog.Tools.Count}");
        Console.WriteLine($"groups:     {string.Join(", ", catalog.Groups.Select(g => $"{g.Tag} ({g.Operations.Count})"))}");
        Console.WriteLine();

        var tools = tool is null
            ? catalog.Tools
            : catalog.Tools.Concat(catalog.OperationTools)
                .Where(t => string.Equals(t.Name, tool, StringComparison.Ordinal))
                .Take(1)
                .ToList();

        foreach (var entry in tools)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = entry.Plan is { } plan ? $"{plan.Method.Method} {plan.PathTemplate}" : entry.Kind.ToString();
            Console.WriteLine($"{entry.Name}  ({target})");

            foreach (var line in entry.Description.Split('\n'))
            {
                Console.WriteLine($"  | {line}");
            }

            foreach (var binding in entry.Plan?.Arguments ?? [])
            {
                var required = binding.Required ? "required" : "optional";
                Console.WriteLine(
                    $"  - {binding.ArgumentName} -> {binding.Location.ToString().ToLowerInvariant()} {binding.ParameterName} ({required})");
            }

            if (entry.Plan?.BodyArgumentName is { } body)
            {
                var required = entry.Plan.BodyRequired ? "required" : "optional";
                Console.WriteLine($"  - {body} -> body {entry.Plan.BodyContentType} ({required})");
            }

            if (schema)
            {
                var rendered = JsonSerializer.Serialize(entry.InputSchema, JsonOutputOptions);
                Console.WriteLine($"  schema ({rendered.Length} chars):");
                foreach (var line in rendered.Split('\n'))
                {
                    Console.WriteLine($"    {line.TrimEnd()}");
                }
            }
            else
            {
                var compact = JsonSerializer.Serialize(entry.InputSchema);
                Console.WriteLine($"  schema: {compact.Length} chars");
            }

            Console.WriteLine();
        }

        return 0;
    }
}
