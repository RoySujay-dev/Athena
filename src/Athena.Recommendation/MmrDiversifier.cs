using Athena.Core;
using Athena.Core.Records;

namespace Athena.Recommendation;

/// <summary>
/// Greedy Maximal Marginal Relevance (brief §9.1). Relevance-only ranking returns five
/// copies of the same document; MMR trades seed-similarity against similarity to what is
/// already selected. Lambda semantics measured on this corpus (REPORT.md §7.1, seeded on B3): 1.0 is
/// pure relevance (B-heavy, lineage twins adjacent before dedup), 0.7 is the usable default
/// (C leaks in), 0.3 lets diversity dominate (cluster A surfaces next to a RAG survey) —
/// nDCG falls as diversity rises, and that trade-off being visible is the point.
///
/// Pure function: no I/O, no clock, no ambient state; the candidate list is never mutated.
/// </summary>
public sealed class MmrDiversifier : IDiversifier
{
    public IReadOnlyList<DocRecord> Select(ReadOnlyMemory<float> seed, IReadOnlyList<DocRecord> candidates,
                                           int topK, double lambda = 0.7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lambda, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lambda, 1.0);

        // Seed similarities are fixed per candidate; compute once, not per greedy round.
        double[] seedSim = new double[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            seedSim[i] = VectorMath.Cosine(candidates[i].Embedding, seed);
        }

        var selected = new List<DocRecord>(Math.Min(topK, candidates.Count));
        // maxSimToSelected[i] = max over selected s of sim(candidate i, s), maintained
        // incrementally: after each pick only the new member can raise it.
        double[] maxSimToSelected = new double[candidates.Count];
        bool[] taken = new bool[candidates.Count];

        while (selected.Count < topK && selected.Count < candidates.Count)
        {
            int best = -1;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (taken[i])
                {
                    continue;
                }

                // Max over an empty selected set is 0, so the first pick reduces to
                // lambda * sim(d, seed): most-seed-similar first for any lambda > 0. At
                // lambda = 0 every first-pick score ties at 0 and the tie-break below makes
                // the outcome well-defined: most relevant first, then farthest-from-selected.
                double score = lambda * seedSim[i] - (1 - lambda) * maxSimToSelected[i];

                // Deterministic tie-break: score desc, then seed-similarity desc, then DocId
                // ordinal — identical inputs always yield identical lists.
                bool beatsBest = best < 0
                    || score > bestScore
                    || (score == bestScore
                        && (seedSim[i] > seedSim[best]
                            || (seedSim[i] == seedSim[best]
                                && string.CompareOrdinal(candidates[i].DocId, candidates[best].DocId) < 0)));
                if (beatsBest)
                {
                    best = i;
                    bestScore = score;
                }
            }

            taken[best] = true;
            selected.Add(candidates[best]);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!taken[i])
                {
                    double sim = VectorMath.Cosine(candidates[i].Embedding, candidates[best].Embedding);
                    if (sim > maxSimToSelected[i])
                    {
                        maxSimToSelected[i] = sim;
                    }
                }
            }
        }

        return selected;
    }
}
