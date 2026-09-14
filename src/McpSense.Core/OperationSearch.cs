using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace McpSense.Core;

/// <summary>One operation matched by a search, with the score that ranked it.</summary>
public sealed class OperationSearchHit
{
    /// <summary>The matched operation.</summary>
    public required OperationDescriptor Operation { get; init; }

    /// <summary>
    /// The relevance score. Scores are only meaningful relative to other hits from the same index.
    /// </summary>
    public required double Score { get; init; }
}

/// <summary>
/// Finds operations matching a natural-language query.
/// </summary>
/// <remarks>
/// This abstraction is what keeps meta-tool mode usable without AI. The mode is triggered by how
/// large a spec is, not by whether an <c>IChatClient</c> or embedding generator was configured, so
/// a default implementation that needs neither has to exist. <see cref="LexicalOperationSearch"/>
/// is that default; McpSense.Ai supplies an embedding-backed implementation for callers who opt in.
/// </remarks>
public interface IOperationSearch
{
    /// <summary>
    /// Returns the best matches for a query, most relevant first.
    /// </summary>
    /// <param name="query">What the caller is looking for, in natural language.</param>
    /// <param name="limit">The most hits to return.</param>
    /// <param name="tag">When set, only consider operations carrying this tag.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    ValueTask<IReadOnlyList<OperationSearchHit>> SearchAsync(
        string query,
        int limit,
        string? tag = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Token-overlap search over each operation's name, summary, description, path and tags.
/// </summary>
/// <remarks>
/// Scoring is deliberately simple: a query token that appears in an operation's text contributes,
/// weighted by how rare that token is across the whole spec, and matches in the name or path count
/// for more than matches in prose. This finds an operation when the caller already knows roughly
/// what it is called, which covers most of what a model needs. It cannot match on meaning — the
/// embedding-backed implementation in McpSense.Ai is the upgrade for that.
/// </remarks>
public sealed class LexicalOperationSearch : IOperationSearch
{
    private const double IdentifierWeight = 3.0;

    private readonly List<Document> _documents;
    private readonly Dictionary<string, double> _inverseFrequency;

    /// <summary>Indexes the given operations.</summary>
    public LexicalOperationSearch(IReadOnlyList<OperationDescriptor> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        _documents = operations.Select(Document.From).ToList();

        var documentFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in _documents)
        {
            foreach (var token in document.AllTokens)
            {
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
            }
        }

        // Rare tokens identify an operation; tokens present in most operations say almost nothing.
        _inverseFrequency = documentFrequency.ToDictionary(
            static entry => entry.Key,
            entry => Math.Log(1.0 + ((double)_documents.Count / entry.Value)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OperationSearchHit>> SearchAsync(
        string query,
        int limit,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var queryTokens = Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var hits = _documents
            .Where(document => tag is null || document.Tags.Contains(tag, StringComparer.Ordinal))
            .Select(document => new OperationSearchHit
            {
                Operation = document.Operation,
                Score = Score(document, queryTokens),
            })
            .Where(static hit => hit.Score > 0)
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Operation.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<OperationSearchHit>>(hits);
    }

    private double Score(Document document, IReadOnlyList<string> queryTokens)
    {
        var score = 0.0;
        foreach (var token in queryTokens)
        {
            var weight = _inverseFrequency.GetValueOrDefault(token, 1.0);

            if (document.IdentifierTokens.Contains(token))
            {
                score += weight * IdentifierWeight;
            }
            else if (document.ProseTokens.Contains(token))
            {
                score += weight;
            }
        }

        return score;
    }

    /// <summary>
    /// Splits text into lowercase word tokens, breaking identifiers on case changes and on the
    /// separators specs use, so <c>listPets</c> and <c>list_pets</c> both yield "list" and "pets".
    /// </summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var current = new List<char>();

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                // A lowercase-to-uppercase boundary marks a new word inside an identifier.
                if (char.IsUpper(c) && current.Count > 0 && char.IsLower(current[^1]))
                {
                    yield return new string([.. current]).ToLowerInvariant();
                    current.Clear();
                }

                current.Add(c);
            }
            else if (current.Count > 0)
            {
                yield return new string([.. current]).ToLowerInvariant();
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            yield return new string([.. current]).ToLowerInvariant();
        }
    }

    private sealed class Document
    {
        public required OperationDescriptor Operation { get; init; }

        /// <summary>Tokens from the name, path and tags, which name the operation.</summary>
        public required HashSet<string> IdentifierTokens { get; init; }

        /// <summary>Tokens from the summary and description.</summary>
        public required HashSet<string> ProseTokens { get; init; }

        public required IReadOnlyList<string> Tags { get; init; }

        public IEnumerable<string> AllTokens => IdentifierTokens.Concat(ProseTokens).Distinct(StringComparer.OrdinalIgnoreCase);

        public static Document From(OperationDescriptor operation)
        {
            var identifier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in Tokenize(operation.Name).Concat(Tokenize(operation.PathTemplate)))
            {
                identifier.Add(token);
            }

            foreach (var tag in operation.Tags)
            {
                foreach (var token in Tokenize(tag))
                {
                    identifier.Add(token);
                }
            }

            var prose = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in Tokenize($"{operation.Summary} {operation.Description}"))
            {
                // A token that already names the operation should not score twice.
                if (!identifier.Contains(token))
                {
                    prose.Add(token);
                }
            }

            return new Document
            {
                Operation = operation,
                IdentifierTokens = identifier,
                ProseTokens = prose,
                Tags = operation.Tags,
            };
        }
    }
}
