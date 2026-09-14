using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McpSense.Core;

/// <summary>What a tool in the catalog does.</summary>
public enum CatalogToolKind
{
    /// <summary>Calls one API operation directly.</summary>
    Operation,

    /// <summary>Finds operations matching a query.</summary>
    SearchOperations,

    /// <summary>Returns the full definition of one operation.</summary>
    DescribeOperation,

    /// <summary>Calls an operation named at call time.</summary>
    CallOperation,
}

/// <summary>
/// One tool as it will be advertised to an MCP client.
/// </summary>
public sealed class CatalogTool
{
    /// <summary>The tool name, unique within the catalog.</summary>
    public required string Name { get; init; }

    /// <summary>The description shown to the model.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The JSON Schema for the tool's arguments. Always a self-contained <c>object</c> schema:
    /// local <c>$ref</c>s from the spec are inlined, because an MCP client has no access to the
    /// OpenAPI document they point into.
    /// </summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>What this tool does.</summary>
    public required CatalogToolKind Kind { get; init; }

    /// <summary>
    /// How to turn a call into an HTTP request. Set only when <see cref="Kind"/> is
    /// <see cref="CatalogToolKind.Operation"/>; the meta-tools carry no plan of their own.
    /// </summary>
    public ToolInvocationPlan? Plan { get; init; }
}

/// <summary>
/// Builds the search index over the operations that survived filtering.
/// </summary>
/// <param name="operations">The operations to index.</param>
/// <param name="cancellationToken">A cancellation token.</param>
public delegate ValueTask<IOperationSearch> OperationSearchFactory(
    IReadOnlyList<OperationDescriptor> operations,
    CancellationToken cancellationToken);

/// <summary>
/// The tools served for one OpenAPI document.
/// </summary>
/// <remarks>
/// In <see cref="ToolCatalogMode.Direct"/> mode the advertised tools are the operation tools. In
/// <see cref="ToolCatalogMode.MetaTool"/> mode only the meta-tools are advertised, while the
/// operation tools remain reachable through <see cref="TryGetOperation"/> so that
/// <c>call_operation</c> can invoke them.
/// </remarks>
public sealed class ToolCatalog
{
    private readonly Dictionary<string, CatalogTool> _advertisedByName;
    private readonly Dictionary<string, CatalogTool> _operationsByName;

    /// <summary>Creates a catalog.</summary>
    /// <param name="tools">The tools to advertise, in listing order.</param>
    /// <param name="operationTools">Every operation tool, advertised or not.</param>
    /// <param name="mode">How the operations are presented.</param>
    /// <param name="groups">The operations grouped by their primary tag.</param>
    /// <param name="search">The index backing <c>search_operations</c>.</param>
    public ToolCatalog(
        IReadOnlyList<CatalogTool> tools,
        IReadOnlyList<CatalogTool> operationTools,
        ToolCatalogMode mode,
        IReadOnlyList<OperationGroup> groups,
        IOperationSearch search)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(operationTools);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(search);

        Tools = tools;
        OperationTools = operationTools;
        Mode = mode;
        Groups = groups;
        Search = search;

        _advertisedByName = new Dictionary<string, CatalogTool>(tools.Count, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            _advertisedByName[tool.Name] = tool;
        }

        _operationsByName = new Dictionary<string, CatalogTool>(operationTools.Count, StringComparer.Ordinal);
        foreach (var tool in operationTools)
        {
            _operationsByName[tool.Name] = tool;
        }
    }

    /// <summary>The tools advertised to the client, in listing order.</summary>
    public IReadOnlyList<CatalogTool> Tools { get; }

    /// <summary>Every operation tool, whether or not it is advertised.</summary>
    public IReadOnlyList<CatalogTool> OperationTools { get; }

    /// <summary>How the operations are presented.</summary>
    public ToolCatalogMode Mode { get; }

    /// <summary>The operations grouped by their primary tag.</summary>
    public IReadOnlyList<OperationGroup> Groups { get; }

    /// <summary>The index backing <c>search_operations</c>.</summary>
    public IOperationSearch Search { get; }

    /// <summary>Looks up a tool the client was told about.</summary>
    public bool TryGet(string name, out CatalogTool tool) => _advertisedByName.TryGetValue(name, out tool!);

    /// <summary>Looks up an operation tool by name, advertised or not.</summary>
    public bool TryGetOperation(string name, out CatalogTool tool) => _operationsByName.TryGetValue(name, out tool!);
}
