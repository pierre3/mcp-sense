using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpSense.Core;
using McpSense.Server.Authentication;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpSense.Server;

/// <summary>
/// Serves a <see cref="ToolCatalog"/> through the MCP low-level tool handlers.
/// </summary>
/// <remarks>
/// The handlers are implemented by hand rather than through <c>AIFunctionFactory</c> or
/// <c>McpServerTool.Create</c>. Those APIs derive a tool's schema by reflecting over a C# delegate
/// at compile time, whereas McpSense only learns the shape of its tools at run time, from the spec
/// it was pointed at.
/// </remarks>
public sealed class ToolCatalogHandlers
{
    private const int DefaultSearchLimit = 10;

    private static readonly JsonSerializerOptions SchemaFormatting = new() { WriteIndented = true };

    private readonly ToolCatalog _catalog;
    private readonly ApiDispatcher _dispatcher;

    /// <summary>Creates the handlers.</summary>
    /// <param name="catalog">The tools to serve.</param>
    /// <param name="dispatcher">Used to invoke the operation behind a tool.</param>
    public ToolCatalogHandlers(ToolCatalog catalog, ApiDispatcher dispatcher)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Answers <c>tools/list</c> with whatever the catalog advertises.</summary>
    public ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(ListTools());

    /// <summary>Answers <c>tools/call</c>, either dispatching to the API or serving a meta-tool.</summary>
    public ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
        => InvokeAsync(request.Params?.Name, request.Params?.Arguments, cancellationToken);

    /// <summary>
    /// Builds the advertised tool list. Separate from the protocol handler so the catalog's
    /// behaviour can be exercised without standing up a server.
    /// </summary>
    public ListToolsResult ListTools()
    {
        var tools = _catalog.Tools
            .Select(static tool => new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
            })
            .ToList();

        return new ListToolsResult { Tools = tools };
    }

    /// <summary>
    /// Handles one tool call. Separate from the protocol handler so the behaviour can be exercised
    /// without standing up a server.
    /// </summary>
    /// <param name="name">The tool being called.</param>
    /// <param name="arguments">Arguments supplied by the client, if any.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async ValueTask<CallToolResult> InvokeAsync(
        string? name,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default)
    {
        if (name is null || !_catalog.TryGet(name, out var tool))
        {
            return Failure($"Unknown tool '{name}'.");
        }

        try
        {
            return tool.Kind switch
            {
                CatalogToolKind.SearchOperations => await SearchAsync(arguments, cancellationToken).ConfigureAwait(false),
                CatalogToolKind.DescribeOperation => Describe(arguments),
                CatalogToolKind.CallOperation => await CallOperationAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => await DispatchAsync(tool, arguments, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (ToolInvocationException ex)
        {
            // A bad call is the model's to fix, so report it as a tool error rather than letting
            // it surface as a protocol-level failure.
            return Failure(ex.Message);
        }
        catch (AccessTokenUnavailableException ex)
        {
            // Reaching the user for consent needs a browser, which a stdio server has no way to
            // open. Say what has to happen instead of failing opaquely.
            return Failure($"Not authorized to call this API: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Failure($"The request to the upstream API failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("The request to the upstream API timed out.");
        }
    }

    private async ValueTask<CallToolResult> DispatchAsync(
        CatalogTool tool,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        var result = await _dispatcher.InvokeAsync(tool, arguments, cancellationToken).ConfigureAwait(false);

        return new CallToolResult
        {
            IsError = !result.IsSuccess,
            Content = [new TextContentBlock { Text = result.Content }],
        };
    }

    private async ValueTask<CallToolResult> SearchAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return Failure("Argument 'query' is required and must not be empty.");
        }

        var tag = GetString(arguments, "tag");
        var limit = GetInt32(arguments, "limit") ?? DefaultSearchLimit;

        var hits = await _catalog.Search.SearchAsync(query, limit, tag, cancellationToken).ConfigureAwait(false);

        if (hits.Count == 0)
        {
            var groups = string.Join(", ", _catalog.Groups.Select(static group => group.Tag));
            return Success(
                $"No operation matched \"{query}\"" + (tag is null ? "." : $" within the group \"{tag}\".")
                + $"\n\nAvailable groups: {groups}");
        }

        var builder = new StringBuilder();
        builder.Append(hits.Count).Append(" match(es) for \"").Append(query).Append("\":\n");

        foreach (var hit in hits)
        {
            var operation = hit.Operation;
            builder.Append('\n').Append(operation.Name)
                .Append("  (").Append(operation.Method.Method).Append(' ').Append(operation.PathTemplate).Append(')');

            var summary = operation.Summary ?? FirstLine(operation.Description);
            if (summary is not null)
            {
                builder.Append("\n    ").Append(summary);
            }

            if (operation.Tags.Count > 0)
            {
                builder.Append("\n    tags: ").Append(string.Join(", ", operation.Tags));
            }

            builder.Append('\n');
        }

        builder.Append("\nCall ").Append(MetaToolNames.DescribeOperation)
            .Append(" with one of these names to see its arguments.");

        return Success(builder.ToString());
    }

    private CallToolResult Describe(IDictionary<string, JsonElement>? arguments)
    {
        var name = GetString(arguments, MetaToolNames.OperationArgument);
        if (name is null)
        {
            return Failure($"Argument '{MetaToolNames.OperationArgument}' is required.");
        }

        if (!_catalog.TryGetOperation(name, out var tool))
        {
            return Failure(UnknownOperationMessage(name));
        }

        var operation = tool.Plan!.Operation;
        var builder = new StringBuilder();

        builder.Append(tool.Name).Append("  (").Append(operation.Method.Method).Append(' ')
            .Append(operation.PathTemplate).Append(")\n\n")
            .Append(tool.Description).Append("\n\n");

        if (operation.Security.Count > 0)
        {
            var schemes = string.Join(
                " or ",
                operation.Security.Select(static requirement =>
                    string.Join(" and ", requirement.Schemes.Select(static scheme => scheme.SchemeName))));
            builder.Append("Authentication: ").Append(schemes)
                .Append(" (supplied by the server; do not pass credentials as arguments).\n\n");
        }

        builder.Append("Arguments (JSON Schema):\n")
            .Append(JsonSerializer.Serialize(tool.InputSchema, SchemaFormatting))
            .Append("\n\nCall it with ").Append(MetaToolNames.CallOperation)
            .Append(" using {\"").Append(MetaToolNames.OperationArgument).Append("\": \"").Append(tool.Name)
            .Append("\", \"").Append(MetaToolNames.ArgumentsArgument).Append("\": { ... }}.");

        return Success(builder.ToString());
    }

    private async ValueTask<CallToolResult> CallOperationAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        var name = GetString(arguments, MetaToolNames.OperationArgument);
        if (name is null)
        {
            return Failure($"Argument '{MetaToolNames.OperationArgument}' is required.");
        }

        if (!_catalog.TryGetOperation(name, out var tool))
        {
            return Failure(UnknownOperationMessage(name));
        }

        IDictionary<string, JsonElement>? inner = null;
        if (arguments is not null &&
            arguments.TryGetValue(MetaToolNames.ArgumentsArgument, out var nested) &&
            nested.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (nested.ValueKind != JsonValueKind.Object)
            {
                return Failure($"Argument '{MetaToolNames.ArgumentsArgument}' must be an object.");
            }

            inner = nested.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => property.Value,
                StringComparer.Ordinal);
        }

        return await DispatchAsync(tool, inner, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explains a bad operation name, pointing back at search rather than dumping every name: in
    /// meta-tool mode there are too many to list usefully.
    /// </summary>
    private string UnknownOperationMessage(string name)
        => $"No operation named '{name}'. Use {MetaToolNames.SearchOperations} to find the right name.";

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    private static string? GetString(IDictionary<string, JsonElement>? arguments, string name)
        => arguments is not null &&
           arguments.TryGetValue(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(IDictionary<string, JsonElement>? arguments, string name)
        => arguments is not null &&
           arguments.TryGetValue(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;

    private static CallToolResult Success(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
    };

    private static CallToolResult Failure(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
