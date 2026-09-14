using System.Text.Json;
using System.Text.Json.Nodes;
using Cocona;
using McpSense.Core;
using Microsoft.OpenApi;

namespace McpSense.Tool.Commands;

/// <summary>
/// Debug command that prints what <see cref="OperationModelBuilder"/> extracted from a spec.
/// It exists so the parsing pipeline can be checked against real-world specs without standing up
/// an MCP server.
/// </summary>
public sealed class DumpOperationsCommand
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Parses a spec and prints its operations.
    /// </summary>
    [Command("dump-operations", Description = "Parse an OpenAPI spec and print the operations McpSense extracts from it.")]
    public async Task<int> RunAsync(
        [Argument(Description = "Path or URL of the OpenAPI spec.")] string spec,
        [Option("json", Description = "Emit JSON instead of the human-readable table.")] bool json = false,
        [Option("detail", Description = "Include parameters and request body details in the text output.")] bool detail = false,
        [Option("tag", Description = "Only show operations carrying this tag.")] string? tag = null,
        CancellationToken cancellationToken = default)
    {
        OpenApiSpecLoadResult loaded;
        try
        {
            loaded = await OpenApiSpecLoader.LoadAsync(spec, cancellationToken);
        }
        catch (OpenApiSpecLoadException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            foreach (var problem in ex.Problems)
            {
                await Console.Error.WriteLineAsync($"  {problem}");
            }

            return 1;
        }

        var operations = new OperationModelBuilder().Build(loaded.Document);

        if (tag is not null)
        {
            operations = operations
                .Where(o => o.Tags.Contains(tag, StringComparer.Ordinal))
                .ToList();
        }

        if (json)
        {
            await WriteJsonAsync(operations, cancellationToken);
        }
        else
        {
            await WriteTextAsync(spec, loaded, operations, detail, cancellationToken);
        }

        return 0;
    }

    private static async Task WriteTextAsync(
        string spec,
        OpenApiSpecLoadResult loaded,
        IReadOnlyList<OperationDescriptor> operations,
        bool detail,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"spec:       {spec}");
        Console.WriteLine($"version:    {loaded.SpecVersion}");
        Console.WriteLine($"title:      {loaded.Document.Info?.Title ?? "(none)"}");
        Console.WriteLine($"operations: {operations.Count}");

        var distinctTags = operations.SelectMany(o => o.Tags).Distinct(StringComparer.Ordinal).Count();
        Console.WriteLine($"tags:       {distinctTags}");

        var missingOperationIds = operations.Count(o => o.OperationId is null);
        if (missingOperationIds > 0)
        {
            Console.WriteLine($"note:       {missingOperationIds} operation(s) had no operationId; names were synthesised.");
        }

        // Validation findings are non-fatal; report them so spec problems stay visible.
        if (loaded.Problems.Count > 0)
        {
            Console.WriteLine($"problems:   {loaded.Problems.Count} (non-fatal validation findings)");
            foreach (var problem in loaded.Problems)
            {
                Console.WriteLine($"  {problem}");
            }
        }

        if (loaded.Warnings.Count > 0)
        {
            Console.WriteLine($"warnings:   {loaded.Warnings.Count}");
            foreach (var warning in loaded.Warnings)
            {
                Console.WriteLine($"  {warning}");
            }
        }

        Console.WriteLine();

        var nameWidth = operations.Count == 0 ? 4 : Math.Min(operations.Max(o => o.Name.Length), 48);

        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tags = operation.Tags.Count == 0 ? "-" : string.Join(",", operation.Tags);
            var body = operation.RequestBody is null ? "-" : operation.RequestBody.ContentType;
            var auth = DescribeSecurity(operation.Security);
            var deprecated = operation.Deprecated ? " (deprecated)" : string.Empty;

            Console.WriteLine(
                $"{operation.Name.PadRight(nameWidth)}  {operation.Method.Method,-6} {operation.PathTemplate}{deprecated}");
            Console.WriteLine(
                $"{new string(' ', nameWidth)}  tags={tags}  params={operation.Parameters.Count}  body={body}  auth={auth}");

            if (!detail)
            {
                continue;
            }

            foreach (var parameter in operation.Parameters)
            {
                var required = parameter.Required ? "required" : "optional";
                var type = DescribeSchema(parameter.Schema);
                Console.WriteLine(
                    $"{new string(' ', nameWidth)}    {parameter.Location.ToString().ToLowerInvariant(),-6} {parameter.Name} ({required}, {type})");
            }

            if (operation.RequestBody is { } requestBody)
            {
                var required = requestBody.Required ? "required" : "optional";
                Console.WriteLine(
                    $"{new string(' ', nameWidth)}    body   {requestBody.ContentType} ({required}, {DescribeSchema(requestBody.Schema)})");
            }
        }

        await Console.Out.FlushAsync(cancellationToken);
    }

    private static async Task WriteJsonAsync(
        IReadOnlyList<OperationDescriptor> operations,
        CancellationToken cancellationToken)
    {
        var items = new JsonArray();

        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new JsonArray();
            foreach (var parameter in operation.Parameters)
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = parameter.Name,
                    ["in"] = parameter.Location.ToString().ToLowerInvariant(),
                    ["required"] = parameter.Required,
                    ["description"] = parameter.Description,
                    ["schema"] = await SerializeSchemaAsync(parameter.Schema, cancellationToken),
                });
            }

            var security = new JsonArray();
            foreach (var requirement in operation.Security)
            {
                var schemes = new JsonArray();
                foreach (var scheme in requirement.Schemes)
                {
                    schemes.Add(new JsonObject
                    {
                        ["scheme"] = scheme.SchemeName,
                        ["scopes"] = new JsonArray(scheme.Scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                    });
                }

                security.Add(schemes);
            }

            items.Add(new JsonObject
            {
                ["name"] = operation.Name,
                ["operationId"] = operation.OperationId,
                ["method"] = operation.Method.Method,
                ["path"] = operation.PathTemplate,
                ["summary"] = operation.Summary,
                ["description"] = operation.Description,
                ["deprecated"] = operation.Deprecated,
                ["tags"] = new JsonArray(operation.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["parameters"] = parameters,
                ["requestBody"] = operation.RequestBody is null
                    ? null
                    : new JsonObject
                    {
                        ["contentType"] = operation.RequestBody.ContentType,
                        ["required"] = operation.RequestBody.Required,
                        ["description"] = operation.RequestBody.Description,
                        ["schema"] = await SerializeSchemaAsync(operation.RequestBody.Schema, cancellationToken),
                    },
                ["security"] = security,
            });
        }

        Console.WriteLine(items.ToJsonString(JsonOutputOptions));
        await Console.Out.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Renders a schema as a JSON node so the dump shows exactly what M2 will turn into a tool
    /// InputSchema.
    /// </summary>
    private static async Task<JsonNode?> SerializeSchemaAsync(IOpenApiSchema? schema, CancellationToken cancellationToken)
    {
        if (schema is null)
        {
            return null;
        }

        var serialized = await schema.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken);
        return JsonNode.Parse(serialized);
    }

    private static string DescribeSchema(IOpenApiSchema? schema)
    {
        if (schema is null)
        {
            return "no schema";
        }

        var type = schema.Type?.ToString().ToLowerInvariant() ?? "unspecified";
        return schema.Format is null ? type : $"{type}/{schema.Format}";
    }

    private static string DescribeSecurity(IReadOnlyList<SecurityRequirement> security)
    {
        if (security.Count == 0)
        {
            return "none";
        }

        // Requirements are ORed, the schemes inside one requirement are ANDed.
        return string.Join(
            " | ",
            security.Select(r => string.Join("+", r.Schemes.Select(s => s.SchemeName))));
    }
}
