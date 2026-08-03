using Athena.Core.Records;

namespace Athena.Eval.Metrics;

/// <summary>
/// Distinct documents recommended across ALL seeds ÷ corpus size (§11.2). A whole-run
/// property: per-case values are the running cumulative fraction, and
/// <see cref="Aggregate"/> reports the final one — the harness's default mean would be
/// meaningless here, which is exactly why IAggregatedMetric exists. Stateful across one run
/// by design; the factory creates a fresh instance per configuration, so ablation arms never
/// share coverage state.
/// </summary>
public sealed class CatalogueCoverage : IMetric<RecCase>, IAggregatedMetric
{
    private readonly IRecommendationSource _recommendations;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public CatalogueCoverage(IRecommendationSource recommendations)
    {
        _recommendations = recommendations;
    }

    public string Name => "CatalogueCoverage";

    public async Task<double> ComputeAsync(RecCase testCase, CancellationToken ct = default)
    {
        IReadOnlyList<DocRecord> recommended = await _recommendations.GetAsync(testCase.SeedDocId, ct);
        foreach (DocRecord doc in recommended)
        {
            _seen.Add(doc.DocId);
        }

        return (double)_seen.Count / _recommendations.CorpusSize;
    }

    /// <summary>The last cumulative value IS the run's coverage.</summary>
    public double Aggregate(IReadOnlyList<double> caseValues)
        => caseValues.Count == 0 ? 0 : caseValues[^1];
}
