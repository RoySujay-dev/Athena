using Athena.Core.Records;

namespace Athena.Eval.Metrics;

/// <summary>
/// nDCG@k with binary relevance against the hand-labelled RecCase (brief §11.2). Ideal DCG
/// places min(k, |relevant|) relevant docs at the top, so a seed with only two useful
/// follow-ups can still score 1.0 — the metric grades ordering quality, not label count.
/// </summary>
public sealed class NdcgAtK : IMetric<RecCase>
{
    private readonly IRecommendationSource _recommendations;
    private readonly int _k;

    public NdcgAtK(IRecommendationSource recommendations, int k = 5)
    {
        _recommendations = recommendations;
        _k = k;
        Name = $"nDCG@{k}";
    }

    public string Name { get; }

    public async Task<double> ComputeAsync(RecCase testCase, CancellationToken ct = default)
    {
        IReadOnlyList<DocRecord> recommended = await _recommendations.GetAsync(testCase.SeedDocId, ct);
        return Compute(
            recommended.Select(d => d.DocId).ToList(),
            testCase.RelevantDocIds.ToHashSet(StringComparer.Ordinal),
            _k);
    }

    /// <summary>Pure: DCG/IDCG, log2 position discount, rank i (1-based) worth 1/log2(i+1).</summary>
    internal static double Compute(IReadOnlyList<string> recommended, IReadOnlySet<string> relevant, int k)
    {
        if (relevant.Count == 0)
        {
            return double.NaN; // an unlabelled seed grades nothing
        }

        double dcg = 0;
        for (int i = 0; i < Math.Min(k, recommended.Count); i++)
        {
            if (relevant.Contains(recommended[i]))
            {
                dcg += 1.0 / Math.Log2(i + 2);
            }
        }

        double idcg = 0;
        for (int i = 0; i < Math.Min(k, relevant.Count); i++)
        {
            idcg += 1.0 / Math.Log2(i + 2);
        }

        return dcg / idcg;
    }
}
