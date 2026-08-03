using Athena.Core;
using Athena.Core.Records;
using Athena.Retrieval;

namespace Athena.Recommendation;

/// <summary>Tunables for the §9.3 blend, exposed so the Part F ablation can sweep them.</summary>
public sealed record RecommendationScorerOptions
{
    /// <summary>
    /// w1/w2/w3 = 0.5/0.3/0.2. Doc similarity carries the most weight because it is the only
    /// signal defined for every candidate on every query; chunk aggregation is a strong
    /// discriminator but zero for most docs on a focused query; recency is a nudge, never a
    /// ranker. To be defended against the Part F weight ablation, not asserted.
    /// </summary>
    public double DocSimWeight { get; init; } = 0.5;

    public double ChunkAggregateWeight { get; init; } = 0.3;

    public double RecencyWeight { get; init; } = 0.2;

    /// <summary>Same k as retrieval fusion (RRF, k=60) — one damping story across the system.</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>
    /// Cap on counted hits per document. Four strong hits should beat one lucky chunk, but
    /// beyond a handful more hits signal document LENGTH, not extra depth — the cap bounds
    /// how much of the list a single document can absorb.
    /// </summary>
    public int MaxHitsPerDoc { get; init; } = 5;

    /// <summary>
    /// Recency time constant. tau=365 gives a sensible draft-vs-final preference (A1/A2 are
    /// ~7 months apart) but effectively deletes the 2020 foundational RAG paper — B1 vs C3
    /// are ~5 years apart and exp(-1800/365) ≈ 0.007. tau≈1000 keeps the draft/final nudge
    /// (ratio ≈ 0.81) while a 5-year-old foundational paper retains exp(-1800/1000) ≈ 0.17
    /// of the decaying term before the floor.
    /// </summary>
    public double TauDays { get; init; } = 1000;

    /// <summary>
    /// recency = floor + (1 - floor) * exp(-ageDays/tau). The floor stops recency from ever
    /// zeroing a document out: an analyst genuinely should be pointed at the foundational
    /// paper, however old it gets.
    /// </summary>
    public double RecencyFloor { get; init; } = 0.3;

    public static RecommendationScorerOptions Default { get; } = new();
}

/// <summary>
/// The §9.3 three-signal blend. Pure function: no I/O, no clock (asOf is an argument), no
/// ambient state. Returns EVERY candidate scored, ranked best-first — MMR and dedup decide
/// what actually surfaces.
/// </summary>
public sealed class RecommendationScorer : IRecommendationScorer
{
    private readonly RecommendationScorerOptions _options;

    public RecommendationScorer(RecommendationScorerOptions? options = null)
    {
        _options = options ?? RecommendationScorerOptions.Default;
    }

    public IReadOnlyList<Recommendation> Score(string query, ReadOnlyMemory<float> queryVector,
                                               IReadOnlyList<DocRecord> candidates,
                                               IReadOnlyList<Passage> chunkHits,
                                               DateTimeOffset asOf)
    {
        // --- chunk aggregation, per doc ---
        // Raw hit COUNTS hand the list to the 30-page survey (a doc with 6x the chunks has
        // ~6x the chances to be hit), so hits are rank-weighted RRF-style (sum 1/(k+rank)),
        // capped at MaxHitsPerDoc, then divided by sqrt(chunkCount). Square-root because
        // LINEAR normalisation over-corrects the other way and hands the list to the 3-page
        // docs — sqrt splits the difference and the choice is stated here to be defended.
        var aggregates = new Dictionary<string, (double Value, int Hits, int BestRank)>(StringComparer.Ordinal);
        for (int rank = 1; rank <= chunkHits.Count; rank++)
        {
            Passage hit = chunkHits[rank - 1];
            (double value, int hits, int bestRank) = aggregates.GetValueOrDefault(hit.DocId);
            if (hits >= _options.MaxHitsPerDoc)
            {
                continue;
            }

            aggregates[hit.DocId] = (
                value + 1.0 / (_options.RrfK + rank),
                hits + 1,
                bestRank == 0 ? rank : bestRank);
        }

        double AggregateFor(DocRecord doc)
        {
            if (!aggregates.TryGetValue(doc.DocId, out var agg))
            {
                return 0;
            }

            // ChunkCount 0 means the doc never ingested chunks it was hit on — defensive 1.
            return agg.Value / Math.Sqrt(Math.Max(1, doc.ChunkCount));
        }

        // Max-normalise the aggregate across THIS candidate set so w2 weighs a [0,1] signal
        // against cosine (~[0,1]) and floored recency ([floor,1]) — the weights stay
        // meaningful instead of fighting three different scales.
        double maxAggregate = candidates.Count == 0 ? 0 : candidates.Max(AggregateFor);

        var results = new List<Recommendation>(candidates.Count);
        foreach (DocRecord doc in candidates)
        {
            double docSim = VectorMath.Cosine(queryVector, doc.Embedding);
            double aggregate = maxAggregate > 0 ? AggregateFor(doc) / maxAggregate : 0;

            double ageDays = Math.Max(0, (asOf - doc.PublishedOn).TotalDays);
            double recency = _options.RecencyFloor
                + (1 - _options.RecencyFloor) * Math.Exp(-ageDays / _options.TauDays);

            double score = _options.DocSimWeight * docSim
                + _options.ChunkAggregateWeight * aggregate
                + _options.RecencyWeight * recency;

            (_, int hits, int bestRank) = aggregates.GetValueOrDefault(doc.DocId);
            results.Add(new Recommendation(
                doc.DocId, doc.Title, Reason: string.Empty, doc.Topics, score,
                new SignalBreakdown(docSim, aggregate, recency, hits, bestRank)));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.DocId, StringComparer.Ordinal) // deterministic on tied scores
            .ToList();
    }
}
