using Microsoft.OpenApi;

namespace McpSense.Core;

/// <summary>
/// The request body of an operation. When the spec defines several media types,
/// <see cref="OperationModelBuilder"/> picks exactly one, preferring JSON.
/// </summary>
public sealed class OperationRequestBody
{
    /// <summary>The selected media type (for example <c>application/json</c>).</summary>
    public required string ContentType { get; init; }

    /// <summary>Whether the body is required.</summary>
    public required bool Required { get; init; }

    /// <summary>The verbatim <c>description</c> from the spec.</summary>
    public string? Description { get; init; }

    /// <summary>The body schema, or <c>null</c> when the spec does not define one.</summary>
    public IOpenApiSchema? Schema { get; init; }
}
