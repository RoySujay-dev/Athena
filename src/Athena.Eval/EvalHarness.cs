namespace Athena.Eval;

/// <summary>
/// Part F console harness (brief §11). Every run — single-config QA, recommender, or ablation
/// grid — evaluates cases × metrics and writes one timestamped CSV to eval/results/ carrying
/// its full configuration. Committed, not screenshotted.
/// </summary>
public sealed class EvalHarness
{
    private readonly IEvalContextFactory _contextFactory;
    private readonly EvalConfig _defaultConfig;
    private readonly string _resultsDirectory;

    public EvalHarness(IEvalContextFactory contextFactory, EvalConfig defaultConfig, string resultsDirectory)
    {
        _contextFactory = contextFactory;
        _defaultConfig = defaultConfig;
        _resultsDirectory = resultsDirectory;
    }

    public Task<EvalReport> RunQaAsync(IReadOnlyList<QaCase> cases, CancellationToken ct = default)
        => RunAsync("qa", [_defaultConfig], cases, [], ct);

    public Task<EvalReport> RunRecommenderAsync(IReadOnlyList<RecCase> cases,
                                                CancellationToken ct = default)
        => RunAsync("recommender", [_defaultConfig], [], cases, ct);

    public Task<EvalReport> RunAblationAsync(AblationGrid grid, CancellationToken ct = default)
        => RunAsync($"ablation-{grid.Name}", grid.Configs, grid.QaCases, grid.RecCases, ct);

    private async Task<EvalReport> RunAsync(
        string kind,
        IReadOnlyList<EvalConfig> configs,
        IReadOnlyList<QaCase> qaCases,
        IReadOnlyList<RecCase> recCases,
        CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var runs = new List<EvalRun>();

        foreach (EvalConfig config in configs)
        {
            ct.ThrowIfCancellationRequested();
            using EvalContext context = await _contextFactory.CreateAsync(config, ct);

            var rows = new List<EvalRow>();
            // Case ids are ordinal over the input list ("qa01"...), so the gold-set file order
            // is the id contract; keep qa-cases.json append-only across runs being compared.
            for (int i = 0; i < qaCases.Count; i++)
            {
                await ComputeRowsAsync(rows, $"qa{i + 1:00}", qaCases[i], context.QaMetrics, ct);
            }

            foreach (RecCase recCase in recCases)
            {
                await ComputeRowsAsync(rows, recCase.SeedDocId, recCase, context.RecMetrics, ct);
            }

            // Run-level number per metric: the NaN-skipping mean, unless the metric declares
            // its own aggregation (IAggregatedMetric — e.g. Catalogue Coverage is cumulative).
            var aggregators = new Dictionary<string, IAggregatedMetric>(StringComparer.Ordinal);
            foreach (IMetric<QaCase> metric in context.QaMetrics)
            {
                if (metric is IAggregatedMetric aggregated)
                {
                    aggregators[metric.Name] = aggregated;
                }
            }

            foreach (IMetric<RecCase> metric in context.RecMetrics)
            {
                if (metric is IAggregatedMetric aggregated)
                {
                    aggregators[metric.Name] = aggregated;
                }
            }

            var aggregates = rows
                .GroupBy(r => r.MetricName, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => aggregators.TryGetValue(g.Key, out IAggregatedMetric? aggregated)
                        ? aggregated.Aggregate(g.Select(r => r.Value).ToList())
                        : g.Average(r => r.Value),
                    StringComparer.Ordinal);
            runs.Add(new EvalRun(config, rows, aggregates));
        }

        string csvPath = EvalCsvWriter.Write(_resultsDirectory, kind, startedAt, runs);
        return new EvalReport(kind, startedAt, runs, csvPath);
    }

    private static async Task ComputeRowsAsync<TCase>(
        List<EvalRow> rows, string caseId, TCase testCase,
        IReadOnlyList<IMetric<TCase>> metrics, CancellationToken ct)
    {
        foreach (IMetric<TCase> metric in metrics)
        {
            double value = await metric.ComputeAsync(testCase, ct);
            if (!double.IsNaN(value)) // NaN = metric not applicable to this case (see IMetric)
            {
                rows.Add(new EvalRow(caseId, metric.Name, value));
            }
        }
    }
}
