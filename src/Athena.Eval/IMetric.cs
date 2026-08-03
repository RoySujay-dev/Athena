namespace Athena.Eval;

/// <summary>
/// One number for one test case (brief §11). A metric that does not apply to a case (e.g.
/// Context Recall on an unanswerable question) returns <see cref="double.NaN"/>; the harness
/// excludes NaN from rows and aggregates rather than letting it poison the mean.
/// </summary>
public interface IMetric<in TCase>
{
    string Name { get; }

    Task<double> ComputeAsync(TCase testCase, CancellationToken ct = default);
}

/// <summary>
/// Extension of the brief's skeleton (README-documented): a metric whose run-level number is
/// NOT the mean of its per-case values. Catalogue Coverage is the motivating case — "distinct
/// documents across all 8 seeds ÷ corpus size" is a property of the whole run, so the metric
/// reports cumulative per-case values and this hook picks the final one. The harness falls
/// back to the NaN-skipping mean for every metric that does not implement this.
/// </summary>
public interface IAggregatedMetric
{
    double Aggregate(IReadOnlyList<double> caseValues);
}
