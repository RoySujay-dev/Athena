using System.Collections.Concurrent;
using Athena.Core.Records;
using Athena.Recommendation;
// The Athena.Recommendation NAMESPACE shadows the Recommendation TYPE here — alias around it.
using Rec = Athena.Recommendation.Recommendation;

namespace Athena.Eval.Metrics;

/// <summary>What every recommender metric grades: the list one seed produced.</summary>
public interface IRecommendationSource
{
    Task<IReadOnlyList<DocRecord>> GetAsync(string seedDocId, CancellationToken ct = default);

    /// <summary>Corpus size — the Catalogue Coverage denominator.</summary>
    int CorpusSize { get; }
}

/// <summary>
/// Memoized more_like_this pipeline on the pure components (scorer → dedup → MMR), mirroring
/// RecommendPlugin's flow without the plugin or a model. One list per seed feeds all five
/// §11.2 metrics, and the ablation knobs arrive as plain values: <paramref name="dedup"/>
/// null = dedup OFF (ablation 4), <paramref name="lambda"/> from the config (ablation 3).
/// </summary>
public sealed class MoreLikeThisRecommendationSource : IRecommendationSource
{
    private readonly IReadOnlyList<DocRecord> _allDocs;
    private readonly IRecommendationScorer _scorer;
    private readonly INearDuplicateResolver? _dedup;
    private readonly IDiversifier _diversifier;
    private readonly double _lambda;
    private readonly int _topK;
    private readonly DateTimeOffset _asOf;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<DocRecord>>>> _cache =
        new(StringComparer.Ordinal);

    public MoreLikeThisRecommendationSource(
        IReadOnlyList<DocRecord> allDocs, IRecommendationScorer scorer,
        INearDuplicateResolver? dedup, IDiversifier diversifier,
        double lambda, int topK, DateTimeOffset asOf)
    {
        _allDocs = allDocs;
        _scorer = scorer;
        _dedup = dedup;
        _diversifier = diversifier;
        _lambda = lambda;
        _topK = topK;
        _asOf = asOf;
    }

    public int CorpusSize => _allDocs.Count;

    public Task<IReadOnlyList<DocRecord>> GetAsync(string seedDocId, CancellationToken ct = default)
        => _cache.GetOrAdd(seedDocId,
            id => new Lazy<Task<IReadOnlyList<DocRecord>>>(() => Task.FromResult(Recommend(id)))).Value;

    private IReadOnlyList<DocRecord> Recommend(string seedDocId)
    {
        DocRecord seed = _allDocs.FirstOrDefault(d => string.Equals(d.DocId, seedDocId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Rec gold set names unknown seed '{seedDocId}'.");

        var candidates = _allDocs.Where(d => !ReferenceEquals(d, seed)).ToList();
        IReadOnlyList<Rec> scored = _scorer.Score(
            seed.Title, seed.Embedding, candidates, chunkHits: [], _asOf);
        var byId = candidates.ToDictionary(d => d.DocId, StringComparer.Ordinal);
        var ranked = scored.Select(r => byId[r.DocId]).ToList();

        IReadOnlyList<DocRecord> resolved = _dedup?.Resolve(ranked, seed) ?? ranked;
        return _diversifier.Select(seed.Embedding, resolved, _topK, _lambda);
    }
}
