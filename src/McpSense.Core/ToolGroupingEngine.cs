using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace McpSense.Core;

/// <summary>A set of operations that share a tag.</summary>
public sealed class OperationGroup
{
    /// <summary>The tag name, or a placeholder for operations that carry no tag.</summary>
    public required string Tag { get; init; }

    /// <summary>The operations in the group, in catalog order.</summary>
    public required IReadOnlyList<OperationDescriptor> Operations { get; init; }
}

/// <summary>The outcome of filtering and grouping.</summary>
public sealed class ToolGrouping
{
    /// <summary>The operations left after allow and deny rules were applied.</summary>
    public required IReadOnlyList<OperationDescriptor> Operations { get; init; }

    /// <summary>Those operations grouped by their primary tag.</summary>
    public required IReadOnlyList<OperationGroup> Groups { get; init; }

    /// <summary>How the operations should be presented.</summary>
    public required ToolCatalogMode Mode { get; init; }
}

/// <summary>
/// Decides which operations to expose and whether to advertise them one by one.
/// </summary>
/// <remarks>
/// <para>
/// Grouping is by the tags the spec already carries, so it costs nothing to compute and matches
/// the vocabulary the API's own documentation uses. An operation's first tag is treated as its
/// primary group, which is the order specs conventionally write them in.
/// </para>
/// <para>
/// Groups are not themselves exposed as tools. They are the table of contents that makes a large
/// spec navigable: they tell the model what territory exists, and they narrow a search.
/// </para>
/// </remarks>
public sealed class ToolGroupingEngine
{
    /// <summary>The group name used for operations that carry no tag at all.</summary>
    public const string UntaggedGroup = "(untagged)";

    /// <summary>
    /// Filters and groups operations, then picks a presentation mode.
    /// </summary>
    /// <param name="operations">The operations modelled from the spec.</param>
    /// <param name="options">Threshold, mode override and allow/deny rules.</param>
    public ToolGrouping Group(IReadOnlyList<OperationDescriptor> operations, ToolGroupingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        options ??= new ToolGroupingOptions();

        var filtered = operations.Where(operation => IsExposed(operation, options)).ToList();

        var groups = filtered
            .GroupBy(PrimaryTag, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new OperationGroup
            {
                Tag = group.Key,
                Operations = group.ToList(),
            })
            .ToList();

        return new ToolGrouping
        {
            Operations = filtered,
            Groups = groups,
            Mode = ResolveMode(filtered.Count, options),
        };
    }

    private static ToolCatalogMode ResolveMode(int operationCount, ToolGroupingOptions options) => options.Mode switch
    {
        ToolCatalogMode.Direct => ToolCatalogMode.Direct,
        ToolCatalogMode.MetaTool => ToolCatalogMode.MetaTool,
        _ => operationCount > options.MetaToolThreshold ? ToolCatalogMode.MetaTool : ToolCatalogMode.Direct,
    };

    private static string PrimaryTag(OperationDescriptor operation)
        => operation.Tags.Count > 0 ? operation.Tags[0] : UntaggedGroup;

    private static bool IsExposed(OperationDescriptor operation, ToolGroupingOptions options)
    {
        if (options.DeniedOperations is { Count: > 0 } deniedOperations &&
            deniedOperations.Any(pattern => MatchesPattern(operation.Name, pattern)))
        {
            return false;
        }

        if (options.DeniedTags is { Count: > 0 } deniedTags &&
            operation.Tags.Any(tag => deniedTags.Contains(tag, StringComparer.Ordinal)))
        {
            return false;
        }

        if (options.AllowedOperations is { Count: > 0 } allowedOperations &&
            !allowedOperations.Any(pattern => MatchesPattern(operation.Name, pattern)))
        {
            return false;
        }

        if (options.AllowedTags is { Count: > 0 } allowedTags &&
            !operation.Tags.Any(tag => allowedTags.Contains(tag, StringComparer.Ordinal)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Matches an operation name against a pattern where <c>*</c> stands for any run of characters.
    /// </summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(name, pattern, StringComparison.Ordinal);
        }

        var regex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
