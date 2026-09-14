using System.Collections.Generic;
using System.Net.Http;

namespace McpSense.Core;

/// <summary>
/// Maps one tool argument onto the API parameter it feeds.
/// </summary>
/// <remarks>
/// Tool arguments are a flat object while OpenAPI parameters are scoped by location, so the two
/// names can differ: when a query and a header parameter share a name, the later one is exposed
/// under a suffixed argument name.
/// </remarks>
public sealed class ToolArgumentBinding
{
    /// <summary>The property name exposed in the tool's input schema.</summary>
    public required string ArgumentName { get; init; }

    /// <summary>The parameter name the API expects.</summary>
    public required string ParameterName { get; init; }

    /// <summary>Where the value goes in the HTTP request.</summary>
    public required OperationParameterLocation Location { get; init; }

    /// <summary>Whether the argument must be supplied.</summary>
    public required bool Required { get; init; }
}

/// <summary>
/// Everything needed to turn a tool call into an HTTP request.
/// </summary>
/// <remarks>
/// Credentials are deliberately absent: they are held by the server and attached at dispatch time,
/// so they never appear in a tool schema and never reach the model.
/// </remarks>
public sealed class ToolInvocationPlan
{
    /// <summary>The HTTP method to use.</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>The path template, with <c>{placeholders}</c> still in place.</summary>
    public required string PathTemplate { get; init; }

    /// <summary>Bindings for every non-body argument.</summary>
    public required IReadOnlyList<ToolArgumentBinding> Arguments { get; init; }

    /// <summary>The argument carrying the request body, or <c>null</c> when there is no body.</summary>
    public string? BodyArgumentName { get; init; }

    /// <summary>The media type to send the body as.</summary>
    public string? BodyContentType { get; init; }

    /// <summary>Whether the body must be supplied.</summary>
    public bool BodyRequired { get; init; }

    /// <summary>The operation this plan came from, kept for diagnostics and logging.</summary>
    public required OperationDescriptor Operation { get; init; }
}
