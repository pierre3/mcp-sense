using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.OpenApi;

namespace McpSense.Core;

/// <summary>
/// Turns operations into the tools an MCP client sees, one tool per operation.
/// </summary>
/// <remarks>
/// <para>
/// Tool arguments are a flat JSON object, so path, query, header and cookie parameters all become
/// sibling properties, and the request body is carried in a single dedicated property (named
/// <c>body</c> unless a parameter already claims that name).
/// </para>
/// <para>
/// Schemas are emitted with local references inlined. An MCP client never sees the OpenAPI
/// document, so a schema containing <c>{"$ref": "#/components/schemas/Pet"}</c> would be
/// meaningless to it.
/// </para>
/// </remarks>
public sealed class ToolCatalogBuilder
{
    private const string DefaultBodyArgumentName = "body";

    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    /// <summary>
    /// Builds a catalog: filters and groups the operations, turns each into a tool, and decides
    /// whether to advertise them directly or behind the meta-tools.
    /// </summary>
    /// <param name="operations">Operations, typically from <see cref="OperationModelBuilder"/>.</param>
    /// <param name="options">Threshold, mode override and allow/deny rules.</param>
    /// <param name="searchFactory">
    /// Builds the index backing <c>search_operations</c>. Defaults to
    /// <see cref="LexicalOperationSearch"/>, which needs no AI.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task<ToolCatalog> BuildAsync(
        IReadOnlyList<OperationDescriptor> operations,
        ToolGroupingOptions? options = null,
        OperationSearchFactory? searchFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var grouping = new ToolGroupingEngine().Group(operations, options);

        var operationTools = new List<CatalogTool>(grouping.Operations.Count);
        foreach (var operation in grouping.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operationTools.Add(await BuildToolAsync(operation, cancellationToken).ConfigureAwait(false));
        }

        var search = searchFactory is null
            ? new LexicalOperationSearch(grouping.Operations)
            : await searchFactory(grouping.Operations, cancellationToken).ConfigureAwait(false);

        var advertised = grouping.Mode == ToolCatalogMode.MetaTool
            ? BuildMetaTools(grouping)
            : operationTools;

        return new ToolCatalog(advertised, operationTools, grouping.Mode, grouping.Groups, search);
    }

    /// <summary>
    /// Builds the three tools that stand in for a full operation list.
    /// </summary>
    private static IReadOnlyList<CatalogTool> BuildMetaTools(ToolGrouping grouping)
    {
        var tags = grouping.Groups.Select(static group => group.Tag).ToList();
        var tagSummary = string.Join(
            ", ",
            grouping.Groups.Select(static group => $"{group.Tag} ({group.Operations.Count})"));

        var tagProperty = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Restrict the search to one group. Groups come from the spec's own tags.",
        };

        if (tags.Count > 0)
        {
            tagProperty["enum"] = new JsonArray([.. tags.Select(static tag => (JsonNode?)JsonValue.Create(tag))]);
        }

        var searchTool = new CatalogTool
        {
            Name = MetaToolNames.SearchOperations,
            Kind = CatalogToolKind.SearchOperations,
            Description =
                $"Find operations in this API by describing what you want to do. This API has "
                + $"{grouping.Operations.Count} operations, too many to list, so start here. "
                + $"Groups: {tagSummary}.\n\n"
                + $"Returns operation names with a one-line summary. Follow up with "
                + $"{MetaToolNames.DescribeOperation} to see an operation's arguments, then "
                + $"{MetaToolNames.CallOperation} to invoke it.",
            InputSchema = ToElement(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "What you want to do, in plain language. For example \"upload a profile picture\".",
                    },
                    ["tag"] = tagProperty,
                    ["limit"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "How many results to return. Defaults to 10.",
                        ["minimum"] = 1,
                        ["maximum"] = 50,
                    },
                },
                ["required"] = new JsonArray("query"),
            }),
        };

        var describeTool = new CatalogTool
        {
            Name = MetaToolNames.DescribeOperation,
            Kind = CatalogToolKind.DescribeOperation,
            Description =
                $"Show one operation in full: what it does, and the exact JSON Schema of the "
                + $"arguments {MetaToolNames.CallOperation} expects for it. "
                + $"Use the operation name returned by {MetaToolNames.SearchOperations}.",
            InputSchema = ToElement(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [MetaToolNames.OperationArgument] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = $"The operation name, as returned by {MetaToolNames.SearchOperations}.",
                    },
                },
                ["required"] = new JsonArray(MetaToolNames.OperationArgument),
            }),
        };

        var callTool = new CatalogTool
        {
            Name = MetaToolNames.CallOperation,
            Kind = CatalogToolKind.CallOperation,
            Description =
                $"Call one of this API's operations. Look its arguments up with "
                + $"{MetaToolNames.DescribeOperation} first: they are validated against that "
                + $"operation's own schema, not against this tool's.",
            InputSchema = ToElement(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [MetaToolNames.OperationArgument] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = $"The operation name, as returned by {MetaToolNames.SearchOperations}.",
                    },
                    [MetaToolNames.ArgumentsArgument] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] =
                            $"The operation's own arguments, shaped as {MetaToolNames.DescribeOperation} reports. "
                            + "Omit for an operation that takes none.",
                    },
                },
                ["required"] = new JsonArray(MetaToolNames.OperationArgument),
            }),
        };

        return [searchTool, describeTool, callTool];
    }

    private static async Task<CatalogTool> BuildToolAsync(
        OperationDescriptor operation,
        CancellationToken cancellationToken)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var bindings = new List<ToolArgumentBinding>(operation.Parameters.Count);
        var usedArgumentNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in operation.Parameters)
        {
            var argumentName = EnsureUniqueArgumentName(parameter.Name, parameter.Location, usedArgumentNames);

            var schema = await RenderSchemaAsync(parameter.Schema, cancellationToken).ConfigureAwait(false)
                ?? new JsonObject { ["type"] = "string" };

            ApplyDescription(schema, parameter.Description, DescribeLocation(parameter.Location, parameter.Name, argumentName));
            properties[argumentName] = schema;

            if (parameter.Required)
            {
                required.Add(argumentName);
            }

            bindings.Add(new ToolArgumentBinding
            {
                ArgumentName = argumentName,
                ParameterName = parameter.Name,
                Location = parameter.Location,
                Required = parameter.Required,
            });
        }

        string? bodyArgumentName = null;
        if (operation.RequestBody is { } requestBody)
        {
            bodyArgumentName = EnsureUnique(DefaultBodyArgumentName, usedArgumentNames);

            var schema = await RenderSchemaAsync(requestBody.Schema, cancellationToken).ConfigureAwait(false)
                ?? new JsonObject { ["type"] = "object" };

            ApplyDescription(schema, requestBody.Description, $"Request body, sent as {requestBody.ContentType}.");
            properties[bodyArgumentName] = schema;

            if (requestBody.Required)
            {
                required.Add(bodyArgumentName);
            }
        }

        var inputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            inputSchema["required"] = required;
        }

        return new CatalogTool
        {
            Name = operation.Name,
            Description = BuildDescription(operation),
            InputSchema = ToElement(inputSchema),
            Kind = CatalogToolKind.Operation,
            Plan = new ToolInvocationPlan
            {
                Method = operation.Method,
                PathTemplate = operation.PathTemplate,
                Arguments = bindings,
                BodyArgumentName = bodyArgumentName,
                BodyContentType = operation.RequestBody?.ContentType,
                BodyRequired = operation.RequestBody?.Required ?? false,
                Operation = operation,
            },
        };
    }

    /// <summary>
    /// Builds the description the model reads. The method and path are included because they carry
    /// real information about what the operation does, and a spec's own prose is often thin.
    /// </summary>
    private static string BuildDescription(OperationDescriptor operation)
    {
        var builder = new StringBuilder();

        if (operation.Summary is not null)
        {
            builder.Append(operation.Summary.TrimEnd());
        }

        if (operation.Description is not null && operation.Description != operation.Summary)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(operation.Description.TrimEnd());
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"Calls {operation.Method.Method} {operation.PathTemplate}.");

        if (operation.Deprecated)
        {
            builder.Append(" This operation is deprecated.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Serialises an OpenAPI schema to JSON Schema with local references inlined, then reparses it
    /// so it can be embedded in the tool's input schema.
    /// </summary>
    private static async Task<JsonObject?> RenderSchemaAsync(IOpenApiSchema? schema, CancellationToken cancellationToken)
    {
        if (schema is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        await schema.SerializeAsync(
                stream,
                OpenApiSpecVersion.OpenApi3_1,
                OpenApiConstants.Json,
                new OpenApiWriterSettings { InlineLocalReferences = true },
                cancellationToken)
            .ConfigureAwait(false);

        stream.Position = 0;
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return node as JsonObject;
    }

    /// <summary>
    /// Adds a description to a schema, keeping the spec's own text when there is one and falling
    /// back to a generated hint that tells the model where the value ends up.
    /// </summary>
    private static void ApplyDescription(JsonObject schema, string? specDescription, string fallback)
    {
        if (specDescription is not null)
        {
            schema["description"] = specDescription;
            return;
        }

        if (schema["description"] is null)
        {
            schema["description"] = fallback;
        }
    }

    private static string DescribeLocation(OperationParameterLocation location, string parameterName, string argumentName)
    {
        var target = string.Equals(parameterName, argumentName, StringComparison.Ordinal)
            ? parameterName
            : $"{parameterName} (exposed as {argumentName})";

        return location switch
        {
            OperationParameterLocation.Path => $"Path parameter {target}.",
            OperationParameterLocation.Query => $"Query parameter {target}.",
            OperationParameterLocation.Header => $"Request header {target}.",
            OperationParameterLocation.Cookie => $"Cookie {target}.",
            _ => $"Parameter {target}.",
        };
    }

    /// <summary>
    /// Picks an argument name for a parameter. Names are kept verbatim where possible; a clash
    /// across locations is resolved by suffixing the location, then a counter.
    /// </summary>
    private static string EnsureUniqueArgumentName(
        string parameterName,
        OperationParameterLocation location,
        HashSet<string> used)
    {
        if (used.Add(parameterName))
        {
            return parameterName;
        }

        var qualified = $"{parameterName}_{location.ToString().ToLowerInvariant()}";
        return used.Add(qualified) ? qualified : EnsureUnique(qualified, used);
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

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>An <c>object</c> schema with no properties, for tools that take no arguments.</summary>
    internal static JsonElement EmptySchema => EmptyObjectSchema;
}
