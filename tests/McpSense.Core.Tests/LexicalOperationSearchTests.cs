using System.Net.Http;
using McpSense.Core;
using Xunit;

namespace McpSense.Core.Tests;

/// <summary>
/// Covers the search that backs meta-tool mode when no embedding model is configured.
/// </summary>
public class LexicalOperationSearchTests
{
    private static OperationDescriptor Operation(
        string name,
        string path,
        string? summary = null,
        string? description = null,
        params string[] tags)
        => new()
        {
            Name = name,
            OperationId = name,
            Method = HttpMethod.Get,
            PathTemplate = path,
            Summary = summary,
            Description = description,
            Deprecated = false,
            Tags = tags,
            Parameters = [],
            Security = [],
        };

    private static readonly IReadOnlyList<OperationDescriptor> Corpus =
    [
        Operation("listPets", "/pets", "List all pets", tags: "pets"),
        Operation("createPet", "/pets", "Add a new pet to the store", tags: "pets"),
        Operation("uploadPetImage", "/pets/{petId}/image", "Upload an image for a pet", tags: "pets"),
        Operation("listOrders", "/orders", "List all orders", tags: "store"),
        Operation("createOrder", "/orders", "Place an order for a pet", tags: "store"),
    ];

    [Theory]
    [InlineData("list pets", "listPets")]
    [InlineData("upload an image", "uploadPetImage")]
    [InlineData("place an order", "createOrder")]
    public async Task RanksTheObviousMatchFirst(string query, string expected)
    {
        var search = new LexicalOperationSearch(Corpus);

        var hits = await search.SearchAsync(query, 5);

        Assert.Equal(expected, hits[0].Operation.Name);
    }

    [Fact]
    public async Task SplitsCamelCaseIdentifiersIntoWords()
    {
        // "uploadPetImage" must be findable by the separate words it is built from.
        var search = new LexicalOperationSearch(Corpus);

        var hits = await search.SearchAsync("image", 5);

        Assert.Equal("uploadPetImage", hits[0].Operation.Name);
    }

    [Fact]
    public async Task RestrictsResultsToATagWhenAsked()
    {
        var search = new LexicalOperationSearch(Corpus);

        var hits = await search.SearchAsync("list", 5, tag: "store");

        Assert.Equal(["listOrders"], hits.Select(h => h.Operation.Name));
    }

    [Fact]
    public async Task HonoursTheResultLimit()
    {
        var search = new LexicalOperationSearch(Corpus);

        var hits = await search.SearchAsync("pet", 2);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task ReturnsNothingWhenNoTokenMatches()
    {
        var search = new LexicalOperationSearch(Corpus);

        var hits = await search.SearchAsync("kubernetes deployment", 5);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task WeighsRareTokensAboveCommonOnes()
    {
        // "pets" appears throughout the corpus while "image" appears once, so a query mentioning
        // both should be pulled towards the rare one.
        var search = new LexicalOperationSearch(Corpus);

        var hits = await search.SearchAsync("pets image", 5);

        Assert.Equal("uploadPetImage", hits[0].Operation.Name);
    }

    [Fact]
    public async Task RanksIdentifierMatchesAboveProseMatches()
    {
        var corpus = new[]
        {
            Operation("archiveRecord", "/archive", "Move a record out of the active set"),
            Operation("listRecords", "/records", "Returns every record, including archive entries"),
        };
        var search = new LexicalOperationSearch(corpus);

        var hits = await search.SearchAsync("archive", 5);

        Assert.Equal("archiveRecord", hits[0].Operation.Name);
    }

    [Fact]
    public async Task IsEmptyForAnEmptyCorpus()
    {
        var search = new LexicalOperationSearch([]);

        Assert.Empty(await search.SearchAsync("anything", 5));
    }
}
