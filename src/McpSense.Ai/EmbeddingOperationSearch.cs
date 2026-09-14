using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpSense.Core;
using Microsoft.Extensions.AI;

namespace McpSense.Ai;

/// <summary>
/// Semantic search over operations, backed by an embedding model.
/// </summary>
/// <remarks>
/// <para>
/// This is the opt-in upgrade to <see cref="LexicalOperationSearch"/>. Token overlap finds an
/// operation when the caller already uses the spec's vocabulary; embeddings find it when they do
/// not — "send someone a picture" reaching an operation the spec calls <c>pushImageMessage</c>.
/// </para>
/// <para>
/// Vectors are held in memory and compared by cosine similarity. A spec has hundreds of operations
/// at most, so a linear scan costs less than the network hop to a vector database would, and the
/// server stays a single process with no external dependency.
/// </para>
/// <para>
/// Embeddings are computed once, when the index is built. Nothing is embedded per request except
/// the query itself.
/// </para>
/// </remarks>
public sealed class EmbeddingOperationSearch : IOperationSearch
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly IReadOnlyList<IndexedOperation> _operations;

    private EmbeddingOperationSearch(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IReadOnlyList<IndexedOperation> operations)
    {
        _generator = generator;
        _operations = operations;
    }

    /// <summary>
    /// Embeds every operation and returns an index over them.
    /// </summary>
    /// <param name="generator">The embedding model to use.</param>
    /// <param name="operations">The operations to index.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<EmbeddingOperationSearch> CreateAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IReadOnlyList<OperationDescriptor> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
        {
            return new EmbeddingOperationSearch(generator, []);
        }

        // One batched call rather than one per operation: generators charge and rate-limit per
        // request, and a large spec would otherwise mean hundreds of round trips at startup.
        var embeddings = await generator
            .GenerateAsync(operations.Select(BuildIndexText).ToList(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var indexed = operations
            .Zip(embeddings, static (operation, embedding) => new IndexedOperation
            {
                Operation = operation,
                Vector = Normalize(embedding.Vector.ToArray()),
            })
            .ToList();

        return new EmbeddingOperationSearch(generator, indexed);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OperationSearchHit>> SearchAsync(
        string query,
        int limit,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (_operations.Count == 0)
        {
            return [];
        }

        var queryEmbedding = await _generator
            .GenerateAsync([query], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var queryVector = Normalize(queryEmbedding[0].Vector.ToArray());

        return _operations
            .Where(entry => tag is null || entry.Operation.Tags.Contains(tag, StringComparer.Ordinal))
            .Select(entry => new OperationSearchHit
            {
                Operation = entry.Operation,
                Score = DotProduct(entry.Vector, queryVector),
            })
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Operation.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    /// <summary>
    /// Builds the text that stands for an operation in the vector space: what it is called, what it
    /// does and where it lives. Parameter schemas are left out deliberately — they describe how to
    /// call an operation, not what it is for, and they would swamp the prose that carries meaning.
    /// </summary>
    private static string BuildIndexText(OperationDescriptor operation)
    {
        var parts = new List<string>(5) { operation.Name };

        if (operation.Summary is not null)
        {
            parts.Add(operation.Summary);
        }

        if (operation.Description is not null && operation.Description != operation.Summary)
        {
            parts.Add(operation.Description);
        }

        parts.Add($"{operation.Method.Method} {operation.PathTemplate}");

        if (operation.Tags.Count > 0)
        {
            parts.Add(string.Join(", ", operation.Tags));
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Scales a vector to unit length so that cosine similarity reduces to a dot product.
    /// </summary>
    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(static value => value * value));
        if (magnitude == 0)
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= magnitude;
        }

        return vector;
    }

    private static double DotProduct(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        var sum = 0.0;
        for (var i = 0; i < length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }

    private sealed class IndexedOperation
    {
        public required OperationDescriptor Operation { get; init; }

        public required float[] Vector { get; init; }
    }
}
