using Athena.Core;
using Athena.Core.Records;

namespace Athena.Eval.Metrics;

/// <summary>
/// 1 − mean pairwise cosine within one returned list (§11.2). This is the number that must
/// RISE as MMR lambda falls — ablation 3's counterweight to nDCG.
/// </summary>
public sealed class IntraListDiversity : IMetric<RecCase>
{
    private readonly IRecommendationSource _recommendations;

    public IntraListDiversity(IRecommendationSource recommendations)
    {
        _recommendations = recommendations;
    }

    public string Name => "IntraListDiversity";

    public async Task<double> ComputeAsync(RecCase testCase, CancellationToken ct = default)
    {
        IReadOnlyList<DocRecord> recommended = await _recommendations.GetAsync(testCase.SeedDocId, ct);
        return Compute(recommended.Select(d => d.Embedding).ToList());
    }

    internal static double Compute(IReadOnlyList<ReadOnlyMemory<float>> embeddings)
    {
        if (embeddings.Count < 2)
        {
            return double.NaN; // pairwise similarity needs a pair
        }

        double sum = 0;
        int pairs = 0;
        for (int i = 0; i < embeddings.Count; i++)
        {
            for (int j = i + 1; j < embeddings.Count; j++)
            {
                sum += VectorMath.Cosine(embeddings[i], embeddings[j]);
                pairs++;
            }
        }

        return 1 - sum / pairs;
    }
}
