using System.Collections.Generic;
using System.Net.Http;

namespace McpSense.Core;

/// <summary>
/// One operation from an OpenAPI document, reduced to what is needed to expose it as an MCP tool.
/// </summary>
/// <remarks>
/// <para>
/// This model is a faithful projection of the spec. It makes no decisions about shortening tool
/// names, rewriting descriptions or grouping operations — those belong to later stages
/// (<c>ToolGroupingEngine</c> and <c>DescriptionEnhancer</c>).
/// </para>
/// <para>
/// The single exception is <see cref="Name"/>: when the spec has no <c>operationId</c>,
/// <see cref="OperationModelBuilder"/> synthesises a deterministic name.
/// </para>
/// </remarks>
public sealed record OperationDescriptor
{
    /// <summary>
    /// A name unique within the catalog. Derived from <c>operationId</c> when present, otherwise
    /// synthesised from the method and path; either way normalised to characters that are valid
    /// in an MCP tool name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The verbatim <c>operationId</c>, or <c>null</c> when the spec does not define one.</summary>
    public string? OperationId { get; init; }

    /// <summary>The HTTP method.</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>The path template (for example <c>/pets/{petId}</c>), kept unexpanded.</summary>
    public required string PathTemplate { get; init; }

    /// <summary>The verbatim <c>summary</c> from the spec.</summary>
    public string? Summary { get; init; }

    /// <summary>The verbatim <c>description</c> from the spec.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the spec marks the operation as deprecated.</summary>
    public required bool Deprecated { get; init; }

    /// <summary>Tag names, the input to tag-based grouping in <c>ToolGroupingEngine</c>.</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>
    /// Parameters with path-item level and operation level already merged. When both define the
    /// same (name, in) pair, the operation level wins.
    /// </summary>
    public required IReadOnlyList<OperationParameter> Parameters { get; init; }

    /// <summary>The request body, or <c>null</c> when the operation takes none.</summary>
    public OperationRequestBody? RequestBody { get; init; }

    /// <summary>
    /// The effective security requirements, ORed together. An empty list means no authentication
    /// is required — including the case where the operation used <c>security: []</c> to explicitly
    /// override the document-level default.
    /// </summary>
    public required IReadOnlyList<SecurityRequirement> Security { get; init; }
}
