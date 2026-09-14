namespace McpSense.Ai;

/// <summary>
/// Controls how <see cref="DescriptionEnhancer"/> rewrites operations.
/// </summary>
public sealed class DescriptionEnhancerOptions
{
    /// <summary>
    /// Whether tool names may be rewritten as well as descriptions.
    /// </summary>
    /// <remarks>
    /// Names carry real weight: they are what a model sees first in a tool list and what
    /// <c>search_operations</c> returns. Specs often supply names built for code generation rather
    /// than for reading, such as <c>activity_list-repos-starred-by-authenticated-user</c>.
    /// </remarks>
    public bool RewriteNames { get; init; } = true;

    /// <summary>
    /// How many operations to rewrite in one model request.
    /// </summary>
    /// <remarks>
    /// Batching is what makes this affordable: a spec with a thousand operations would otherwise
    /// mean a thousand round trips. Sending several together also lets the model keep its wording
    /// consistent across related operations. Too large a batch risks a truncated response.
    /// </remarks>
    public int BatchSize { get; init; } = 10;

    /// <summary>How many batches may be in flight at once.</summary>
    public int MaxConcurrency { get; init; } = 4;
}
