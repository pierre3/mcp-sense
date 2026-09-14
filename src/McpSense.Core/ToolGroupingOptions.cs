using System.Collections.Generic;

namespace McpSense.Core;

/// <summary>How operations are presented to the client.</summary>
public enum ToolCatalogMode
{
    /// <summary>Pick a mode from the number of operations left after filtering.</summary>
    Auto,

    /// <summary>Advertise one tool per operation.</summary>
    Direct,

    /// <summary>
    /// Advertise a small fixed set of meta-tools that search, describe and invoke operations.
    /// </summary>
    MetaTool,
}

/// <summary>
/// Controls which operations are exposed and how.
/// </summary>
public sealed class ToolGroupingOptions
{
    /// <summary>
    /// The operation count above which <see cref="ToolCatalogMode.Auto"/> switches to meta-tool
    /// mode. A tool list much longer than this starts to crowd out the model's context before any
    /// work begins.
    /// </summary>
    public int MetaToolThreshold { get; init; } = 30;

    /// <summary>How to present the operations. Defaults to <see cref="ToolCatalogMode.Auto"/>.</summary>
    public ToolCatalogMode Mode { get; init; } = ToolCatalogMode.Auto;

    /// <summary>
    /// When set, only operations whose name matches one of these patterns are exposed.
    /// Patterns may use <c>*</c> as a wildcard.
    /// </summary>
    public IReadOnlyList<string>? AllowedOperations { get; init; }

    /// <summary>
    /// Operations whose name matches one of these patterns are dropped, even if allowed above.
    /// Patterns may use <c>*</c> as a wildcard.
    /// </summary>
    public IReadOnlyList<string>? DeniedOperations { get; init; }

    /// <summary>When set, only operations carrying one of these tags are exposed.</summary>
    public IReadOnlyList<string>? AllowedTags { get; init; }

    /// <summary>Operations carrying one of these tags are dropped, even if allowed above.</summary>
    public IReadOnlyList<string>? DeniedTags { get; init; }
}
