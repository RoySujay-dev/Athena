using Athena.Core.Records;

namespace Athena.Eval.Metrics;

/// <summary>
/// 1 when a returned list contains two members of one lineage group, else 0; the mean over
/// seeds is §11.2's "fraction of lists in which both members of a lineage pair appear".
/// Target 0. Reads the COMPUTED LineageGroup — never an id list (hard constraint 2).
/// </summary>
public sealed class DuplicateLeakage : IMetric<RecCase>
{
    private readonly IRecommendationSource _recommendations;

    public DuplicateLeakage(IRecommendationSource recommendations)
    {
        _recommendations = recommendations;
    }

    public string Name => "DuplicateLeakage";

    public async Task<double> ComputeAsync(RecCase testCase, CancellationToken ct = default)
    {
        IReadOnlyList<DocRecord> recommended = await _recommendations.GetAsync(testCase.SeedDocId, ct);
        return Compute(recommended);
    }

    internal static double Compute(IReadOnlyList<DocRecord> recommended)
        => recommended
            .Where(d => d.LineageGroup is not null)
            .GroupBy(d => d.LineageGroup, StringComparer.Ordinal)
            .Any(g => g.Count() >= 2)
            ? 1
            : 0;
}
