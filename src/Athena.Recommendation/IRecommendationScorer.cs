using Athena.Core.Records;
using Athena.Retrieval;

namespace Athena.Recommendation;

/// <summary>Blends the three §9.3 signals into one ranked candidate list.</summary>
public interface IRecommendationScorer
{
    /// <summary>
    /// final = w1 * docSim + w2 * normalise(chunkAggregate) + w3 * recency
    /// Fixed weights are acceptable; defended against the Part F ablation.
    /// DEVIATION from the brief's signature (README-documented): <paramref name="asOf"/> is
    /// the recency reference time — pure functions take the clock as input, never read it.
    /// </summary>
    IReadOnlyList<Recommendation> Score(string query, ReadOnlyMemory<float> queryVector,
                                        IReadOnlyList<DocRecord> candidates,
                                        IReadOnlyList<Passage> chunkHits,
                                        DateTimeOffset asOf);
}
