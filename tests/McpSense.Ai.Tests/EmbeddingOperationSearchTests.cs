using System.Net.Http;
using McpSense.Ai;
using McpSense.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace McpSense.Ai.Tests;

/// <summary>
/// Covers the embedding-backed search. A stub generator maps known text to fixed vectors, so the
/// tests check the ranking McpSense does rather than a model's judgement.
/// </summary>
public class EmbeddingOperationSearchTests
{
    private static OperationDescriptor Operation(string name, params string[] tags)
        => new()
        {
            Name = name,
            OperationId = name,
            Method = HttpMethod.Get,
            PathTemplate = $"/{name}",
            Summary = name,
            Deprecated = false,
            Tags = tags,
            Parameters = [],
            Security = [],
        };

    [Fact]
    public async Task RanksByCosineSimilarityToTheQuery()
    {
        var generator = new StubEmbeddingGenerator(text => text switch
        {
            var t when t.Contains("alpha", StringComparison.Ordinal) => [1f, 0f],
            var t when t.Contains("beta", StringComparison.Ordinal) => [0f, 1f],
            _ => [0.7f, 0.7f],
        });

        var search = await EmbeddingOperationSearch.CreateAsync(
            generator,
            [Operation("alpha"), Operation("beta")]);

        var hits = await search.SearchAsync("beta", 2);

        Assert.Equal("beta", hits[0].Operation.Name);
    }

    [Fact]
    public async Task EmbedsEveryOperationInASingleBatchedRequest()
    {
        // A spec with hundreds of operations must not mean hundreds of round trips at startup.
        var generator = new StubEmbeddingGenerator(_ => [1f, 0f]);
        var operations = Enumerable.Range(1, 50).Select(i => Operation($"op{i}")).ToList();

        await EmbeddingOperationSearch.CreateAsync(generator, operations);

        Assert.Equal(1, generator.CallCount);
        Assert.Equal(50, generator.LastBatchSize);
    }

    [Fact]
    public async Task EmbedsOnlyTheQueryPerSearch()
    {
        var generator = new StubEmbeddingGenerator(_ => [1f, 0f]);
        var search = await EmbeddingOperationSearch.CreateAsync(generator, [Operation("alpha")]);
        var afterIndexing = generator.CallCount;

        await search.SearchAsync("anything", 5);

        Assert.Equal(afterIndexing + 1, generator.CallCount);
        Assert.Equal(1, generator.LastBatchSize);
    }

    [Fact]
    public async Task RestrictsResultsToATagWhenAsked()
    {
        var generator = new StubEmbeddingGenerator(_ => [1f, 0f]);
        var search = await EmbeddingOperationSearch.CreateAsync(
            generator,
            [Operation("alpha", "pets"), Operation("beta", "store")]);

        var hits = await search.SearchAsync("anything", 5, tag: "store");

        Assert.Equal(["beta"], hits.Select(h => h.Operation.Name));
    }

    [Fact]
    public async Task HonoursTheResultLimit()
    {
        var generator = new StubEmbeddingGenerator(_ => [1f, 0f]);
        var operations = Enumerable.Range(1, 10).Select(i => Operation($"op{i}")).ToList();
        var search = await EmbeddingOperationSearch.CreateAsync(generator, operations);

        Assert.Equal(3, (await search.SearchAsync("anything", 3)).Count);
    }

    [Fact]
    public async Task IsEmptyForAnEmptyCorpusAndCallsNothing()
    {
        var generator = new StubEmbeddingGenerator(_ => [1f, 0f]);

        var search = await EmbeddingOperationSearch.CreateAsync(generator, []);

        Assert.Empty(await search.SearchAsync("anything", 5));
        Assert.Equal(0, generator.CallCount);
    }

    private sealed class StubEmbeddingGenerator(Func<string, float[]> map)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int CallCount { get; private set; }

        public int LastBatchSize { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var inputs = values.ToList();
            CallCount++;
            LastBatchSize = inputs.Count;

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                inputs.Select(input => new Embedding<float>(map(input)))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
