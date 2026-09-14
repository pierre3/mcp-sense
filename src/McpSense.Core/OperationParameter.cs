using Microsoft.OpenApi;

namespace McpSense.Core;

/// <summary>
/// Where a parameter is carried in the HTTP request.
/// </summary>
/// <remarks>
/// Covers the four OpenAPI <c>in</c> values McpSense supports. <c>querystring</c> (OpenAPI 3.2)
/// is not supported and is skipped by <see cref="OperationModelBuilder"/>.
/// </remarks>
public enum OperationParameterLocation
{
    /// <summary>A placeholder in the path template (<c>petId</c> in <c>/pets/{petId}</c>).</summary>
    Path,

    /// <summary>A query string parameter.</summary>
    Query,

    /// <summary>A request header.</summary>
    Header,

    /// <summary>A cookie carried in the Cookie header.</summary>
    Cookie,
}

/// <summary>
/// A single operation parameter. The request body is modelled separately as
/// <see cref="OperationRequestBody"/>.
/// </summary>
public sealed record OperationParameter
{
    /// <summary>The parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Where the parameter is carried in the request.</summary>
    public required OperationParameterLocation Location { get; init; }

    /// <summary>
    /// Whether the parameter is required. Path parameters are always required per the OpenAPI
    /// specification, so they are reported as required regardless of what the spec says.
    /// </summary>
    public required bool Required { get; init; }

    /// <summary>The verbatim <c>description</c> from the spec.</summary>
    public string? Description { get; init; }

    /// <summary>The parameter schema, or <c>null</c> when the spec does not define one.</summary>
    public IOpenApiSchema? Schema { get; init; }
}
