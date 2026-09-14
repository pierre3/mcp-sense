using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using Microsoft.OpenApi;

namespace McpSense.Core;

/// <summary>
/// Converts an <see cref="OpenApiDocument"/> into a sequence of <see cref="OperationDescriptor"/>.
/// </summary>
/// <remarks>
/// Output order is deterministic: ordinal by path template, then ordinal by HTTP method name. It
/// does not depend on the order operations appear in the spec or on hash ordering, so the same
/// spec always yields the same names in the same sequence.
/// </remarks>
public sealed class OperationModelBuilder
{
    /// <summary>
    /// Models every operation in the document.
    /// </summary>
    /// <param name="document">A parsed OpenAPI document.</param>
    /// <returns>Operations in deterministic order; empty when the document declares no paths.</returns>
    public IReadOnlyList<OperationDescriptor> Build(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var operations = new List<OperationDescriptor>();
        if (document.Paths is null)
        {
            return operations;
        }

        // Names are deduplicated across the whole document, so the numeric suffix on a collision
        // follows this deterministic traversal order.
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pathEntry in document.Paths.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            var pathItem = pathEntry.Value;
            if (pathItem?.Operations is null)
            {
                continue;
            }

            foreach (var operationEntry in pathItem.Operations.OrderBy(static o => o.Key.Method, StringComparer.Ordinal))
            {
                var operation = operationEntry.Value;
                if (operation is null)
                {
                    continue;
                }

                var name = EnsureUnique(BuildName(operation.OperationId, operationEntry.Key, pathEntry.Key), usedNames);

                operations.Add(new OperationDescriptor
                {
                    Name = name,
                    OperationId = NullIfBlank(operation.OperationId),
                    Method = operationEntry.Key,
                    PathTemplate = pathEntry.Key,
                    Summary = NullIfBlank(operation.Summary),
                    Description = NullIfBlank(operation.Description),
                    Deprecated = operation.Deprecated,
                    Tags = BuildTags(operation.Tags),
                    Parameters = BuildParameters(pathItem.Parameters, operation.Parameters),
                    RequestBody = BuildRequestBody(operation.RequestBody),
                    Security = BuildSecurity(operation.Security, document.Security),
                });
            }
        }

        return operations;
    }

    private static IReadOnlyList<string> BuildTags(ISet<OpenApiTagReference>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Preserve the order written in the spec: ToolGroupingEngine treats the first tag as the
        // primary group.
        var result = new List<string>(tags.Count);
        foreach (var tag in tags)
        {
            var name = NullIfBlank(tag?.Name);
            if (name is not null && !result.Contains(name, StringComparer.Ordinal))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Merges path-item level and operation level parameters. Per the OpenAPI specification, the
    /// operation level wins for a given (name, in) pair.
    /// </summary>
    private static IReadOnlyList<OperationParameter> BuildParameters(
        IList<IOpenApiParameter>? pathLevel,
        IList<IOpenApiParameter>? operationLevel)
    {
        var ordered = new List<IOpenApiParameter>();
        var positions = new Dictionary<(string Name, ParameterLocation In), int>();

        void AddOrReplace(IOpenApiParameter? parameter)
        {
            if (parameter is null || string.IsNullOrEmpty(parameter.Name) || parameter.In is not { } location)
            {
                return;
            }

            // Skip locations McpSense cannot carry, such as querystring (OpenAPI 3.2).
            if (!TryMapLocation(location, out _))
            {
                return;
            }

            var key = (parameter.Name, location);
            if (positions.TryGetValue(key, out var existing))
            {
                ordered[existing] = parameter;
            }
            else
            {
                positions[key] = ordered.Count;
                ordered.Add(parameter);
            }
        }

        if (pathLevel is not null)
        {
            foreach (var parameter in pathLevel)
            {
                AddOrReplace(parameter);
            }
        }

        if (operationLevel is not null)
        {
            foreach (var parameter in operationLevel)
            {
                AddOrReplace(parameter);
            }
        }

        if (ordered.Count == 0)
        {
            return Array.Empty<OperationParameter>();
        }

        var result = new List<OperationParameter>(ordered.Count);
        foreach (var parameter in ordered)
        {
            TryMapLocation(parameter.In!.Value, out var location);
            result.Add(new OperationParameter
            {
                Name = parameter.Name!,
                Location = location,
                // Path parameters are always required per the OpenAPI specification, so do not
                // follow a spec that omits `required`.
                Required = location == OperationParameterLocation.Path || parameter.Required,
                Description = NullIfBlank(parameter.Description),
                Schema = parameter.Schema,
            });
        }

        return result;
    }

    private static bool TryMapLocation(ParameterLocation location, out OperationParameterLocation mapped)
    {
        switch (location)
        {
            case ParameterLocation.Path:
                mapped = OperationParameterLocation.Path;
                return true;
            case ParameterLocation.Query:
                mapped = OperationParameterLocation.Query;
                return true;
            case ParameterLocation.Header:
                mapped = OperationParameterLocation.Header;
                return true;
            case ParameterLocation.Cookie:
                mapped = OperationParameterLocation.Cookie;
                return true;
            default:
                mapped = default;
                return false;
        }
    }

    private static OperationRequestBody? BuildRequestBody(IOpenApiRequestBody? requestBody)
    {
        if (requestBody?.Content is null || requestBody.Content.Count == 0)
        {
            return null;
        }

        var contentType = SelectMediaType(requestBody.Content.Keys);
        if (contentType is null)
        {
            return null;
        }

        requestBody.Content.TryGetValue(contentType, out var mediaType);

        return new OperationRequestBody
        {
            ContentType = contentType,
            Required = requestBody.Required,
            Description = NullIfBlank(requestBody.Description),
            Schema = mediaType?.Schema,
        };
    }

    /// <summary>
    /// Picks one media type out of several: plain JSON first, then a <c>+json</c> suffix, and
    /// otherwise the ordinally first entry.
    /// </summary>
    private static string? SelectMediaType(IEnumerable<string> contentTypes)
    {
        var candidates = contentTypes
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .OrderBy(static c => c, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var json = candidates.FirstOrDefault(
            static c => BaseMediaType(c).Equals("application/json", StringComparison.OrdinalIgnoreCase));
        if (json is not null)
        {
            return json;
        }

        var jsonSuffixed = candidates.FirstOrDefault(
            static c => BaseMediaType(c).EndsWith("+json", StringComparison.OrdinalIgnoreCase));
        return jsonSuffixed ?? candidates[0];
    }

    /// <summary>Strips parameters such as "; charset=utf-8" from a media type.</summary>
    private static string BaseMediaType(string contentType)
    {
        var separator = contentType.IndexOf(';');
        return (separator < 0 ? contentType : contentType[..separator]).Trim();
    }

    /// <summary>
    /// Resolves the effective security requirements. Operation-level <c>security</c> wins when
    /// present, otherwise the document default applies. An operation-level <c>security: []</c>
    /// means "explicitly unauthenticated" and cancels the document default.
    /// </summary>
    private static IReadOnlyList<SecurityRequirement> BuildSecurity(
        IList<OpenApiSecurityRequirement>? operationSecurity,
        IList<OpenApiSecurityRequirement>? documentSecurity)
    {
        var effective = operationSecurity ?? documentSecurity;
        if (effective is null || effective.Count == 0)
        {
            return Array.Empty<SecurityRequirement>();
        }

        var requirements = new List<SecurityRequirement>(effective.Count);
        foreach (var requirement in effective)
        {
            if (requirement is null)
            {
                continue;
            }

            var schemes = new List<SecuritySchemeRequirement>(requirement.Count);
            foreach (var entry in requirement)
            {
                var schemeName = NullIfBlank(entry.Key?.Reference?.Id);
                if (schemeName is null)
                {
                    continue;
                }

                schemes.Add(new SecuritySchemeRequirement
                {
                    SchemeName = schemeName,
                    Scopes = entry.Value is { Count: > 0 } scopes ? scopes.ToArray() : Array.Empty<string>(),
                });
            }

            if (schemes.Count > 0)
            {
                requirements.Add(new SecurityRequirement { Schemes = schemes });
            }
        }

        return requirements;
    }

    private static string BuildName(string? operationId, HttpMethod method, string pathTemplate)
    {
        var raw = string.IsNullOrWhiteSpace(operationId)
            ? SynthesizeName(method, pathTemplate)
            : operationId;

        return Sanitize(raw);
    }

    /// <summary>
    /// Builds a deterministic name for specs without an <c>operationId</c>.
    /// For example <c>GET /pets/{petId}/photos</c> becomes <c>get_pets_by_petId_photos</c>.
    /// </summary>
    private static string SynthesizeName(HttpMethod method, string pathTemplate)
    {
        var builder = new StringBuilder();
        builder.Append(method.Method.ToLowerInvariant());

        foreach (var segment in pathTemplate.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append('_');
            if (segment.Length > 2 && segment[0] == '{' && segment[^1] == '}')
            {
                builder.Append("by_").Append(segment[1..^1]);
            }
            else
            {
                builder.Append(segment);
            }
        }

        return builder.ToString();
    }

    /// <summary>Normalises to characters valid in an MCP tool name: letters, digits, underscore and hyphen.</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var sanitized = builder.ToString().Trim('_');
        return sanitized.Length == 0 ? "operation" : sanitized;
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

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
