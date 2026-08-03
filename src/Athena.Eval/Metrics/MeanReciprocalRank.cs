using Athena.Core.Records;

namespace Athena.Eval.Metrics;

/// <summary>Reciprocal rank of the first relevant document in the list; 0 when none appears (§11.2).</summary>
public sealed class MeanReciprocalRank : IMetric<RecCase>
{
    private readonly IRecommendationSource _recommendations;

    public MeanReciprocalRank(IRecommendationSource recommendations)
    {
        _recommendations = recommendations;
    }

    public string Name => "MRR";

    public async Task<double> ComputeAsync(RecCase testCase, CancellationToken ct = default)
    {
        IReadOnlyList<DocRecord> recommended = await _recommendations.GetAsync(testCase.SeedDocId, ct);
        return Compute(
            recommended.Select(d => d.DocId).ToList(),
            testCase.RelevantDocIds.ToHashSet(StringComparer.Ordinal));
    }

    internal static double Compute(IReadOnlyList<string> recommended, IReadOnlySet<string> relevant)
    {
        if (relevant.Count == 0)
        {
            return double.NaN;
        }

        for (int i = 0; i < recommended.Count; i++)
        {
            if (relevant.Contains(recommended[i]))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }
}
