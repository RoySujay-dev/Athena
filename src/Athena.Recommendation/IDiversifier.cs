using Athena.Core.Records;

namespace Athena.Recommendation;

/// <summary>Diversity-aware selection over ranked candidates (brief §9.1).</summary>
public interface IDiversifier
{
    /// <summary>
    /// Maximal Marginal Relevance:
    ///   next = argmax over candidates d of
    ///          lambda * sim(d, seed) - (1 - lambda) * max over selected s of sim(d, s)
    /// </summary>
    IReadOnlyList<DocRecord> Select(ReadOnlyMemory<float> seed, IReadOnlyList<DocRecord> candidates,
                                    int topK, double lambda = 0.7);
}
