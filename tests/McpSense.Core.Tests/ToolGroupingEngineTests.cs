using System.Net.Http;
using McpSense.Core;
using Xunit;

namespace McpSense.Core.Tests;

/// <summary>
/// Covers filtering, tag grouping and the choice between direct and meta-tool mode.
/// </summary>
public class ToolGroupingEngineTests
{
    private static OperationDescriptor Operation(string name, params string[] tags)
        => new()
        {
            Name = name,
            OperationId = name,
            Method = HttpMethod.Get,
            PathTemplate = $"/{name}",
            Deprecated = false,
            Tags = tags,
            Parameters = [],
            Security = [],
        };

    private static IReadOnlyList<OperationDescriptor> Many(int count)
        => Enumerable.Range(1, count).Select(i => Operation($"op{i}", "pets")).ToList();

    [Fact]
    public void StaysInDirectModeAtOrBelowTheThreshold()
    {
        var grouping = new ToolGroupingEngine().Group(Many(30), new ToolGroupingOptions { MetaToolThreshold = 30 });

        Assert.Equal(ToolCatalogMode.Direct, grouping.Mode);
    }

    [Fact]
    public void SwitchesToMetaToolModeAboveTheThreshold()
    {
        var grouping = new ToolGroupingEngine().Group(Many(31), new ToolGroupingOptions { MetaToolThreshold = 30 });

        Assert.Equal(ToolCatalogMode.MetaTool, grouping.Mode);
    }

    [Fact]
    public void CountsOperationsAfterFilteringNotBefore()
    {
        // Denying most of a large spec should bring it back under the threshold.
        var operations = Many(100);
        var grouping = new ToolGroupingEngine().Group(operations, new ToolGroupingOptions
        {
            MetaToolThreshold = 30,
            AllowedOperations = ["op1", "op2", "op3"],
        });

        Assert.Equal(ToolCatalogMode.Direct, grouping.Mode);
        Assert.Equal(3, grouping.Operations.Count);
    }

    [Theory]
    [InlineData(ToolCatalogMode.Direct)]
    [InlineData(ToolCatalogMode.MetaTool)]
    public void HonoursAnExplicitModeRegardlessOfSize(ToolCatalogMode mode)
    {
        var grouping = new ToolGroupingEngine().Group(Many(100), new ToolGroupingOptions
        {
            Mode = mode,
            MetaToolThreshold = 30,
        });

        Assert.Equal(mode, grouping.Mode);
    }

    [Fact]
    public void GroupsByTheFirstTagAndOrdersGroupsDeterministically()
    {
        var operations = new[]
        {
            Operation("a", "zebra"),
            Operation("b", "alpha", "zebra"),
            Operation("c", "alpha"),
        };

        var grouping = new ToolGroupingEngine().Group(operations);

        Assert.Equal(["alpha", "zebra"], grouping.Groups.Select(g => g.Tag));
        Assert.Equal(["b", "c"], grouping.Groups[0].Operations.Select(o => o.Name));
        Assert.Equal(["a"], grouping.Groups[1].Operations.Select(o => o.Name));
    }

    [Fact]
    public void CollectsUntaggedOperationsIntoTheirOwnGroup()
    {
        var grouping = new ToolGroupingEngine().Group([Operation("a"), Operation("b", "pets")]);

        var untagged = Assert.Single(grouping.Groups, g => g.Tag == ToolGroupingEngine.UntaggedGroup);
        Assert.Equal(["a"], untagged.Operations.Select(o => o.Name));
    }

    [Fact]
    public void DropsDeniedOperations()
    {
        var grouping = new ToolGroupingEngine().Group(
            [Operation("listPets"), Operation("deletePet")],
            new ToolGroupingOptions { DeniedOperations = ["deletePet"] });

        Assert.Equal(["listPets"], grouping.Operations.Select(o => o.Name));
    }

    [Fact]
    public void MatchesAllowAndDenyPatternsWithWildcards()
    {
        var operations = new[] { Operation("getPet"), Operation("getOwner"), Operation("deletePet") };

        var grouping = new ToolGroupingEngine().Group(operations, new ToolGroupingOptions
        {
            AllowedOperations = ["get*"],
        });

        Assert.Equal(["getPet", "getOwner"], grouping.Operations.Select(o => o.Name));
    }

    [Fact]
    public void LetsDenyWinOverAllow()
    {
        var operations = new[] { Operation("getPet"), Operation("getOwner") };

        var grouping = new ToolGroupingEngine().Group(operations, new ToolGroupingOptions
        {
            AllowedOperations = ["get*"],
            DeniedOperations = ["getOwner"],
        });

        Assert.Equal(["getPet"], grouping.Operations.Select(o => o.Name));
    }

    [Fact]
    public void FiltersByTagInBothDirections()
    {
        var operations = new[]
        {
            Operation("a", "pets"),
            Operation("b", "admin"),
            Operation("c", "pets", "admin"),
        };

        var allowed = new ToolGroupingEngine().Group(operations, new ToolGroupingOptions { AllowedTags = ["pets"] });
        Assert.Equal(["a", "c"], allowed.Operations.Select(o => o.Name));

        // Deny applies to any tag the operation carries, not just its primary one.
        var denied = new ToolGroupingEngine().Group(operations, new ToolGroupingOptions { DeniedTags = ["admin"] });
        Assert.Equal(["a"], denied.Operations.Select(o => o.Name));
    }
}
