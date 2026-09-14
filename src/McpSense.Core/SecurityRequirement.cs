using System.Collections.Generic;

namespace McpSense.Core;

/// <summary>
/// A requirement on one security scheme.
/// </summary>
public sealed class SecuritySchemeRequirement
{
    /// <summary>
    /// The key under <c>components.securitySchemes</c>. This is the reference name, not a detail
    /// of the scheme itself such as an apiKey header name.
    /// </summary>
    public required string SchemeName { get; init; }

    /// <summary>Required scopes. Empty for schemes other than OAuth2 and OpenID Connect.</summary>
    public required IReadOnlyList<string> Scopes { get; init; }
}

/// <summary>
/// One security requirement. The schemes inside <see cref="Schemes"/> are ANDed together, while
/// the entries of <see cref="OperationDescriptor.Security"/> are ORed: satisfying any one of them
/// is enough.
/// </summary>
public sealed class SecurityRequirement
{
    /// <summary>Schemes that must all be satisfied together.</summary>
    public required IReadOnlyList<SecuritySchemeRequirement> Schemes { get; init; }
}
