using Athena.Eval;

namespace Athena.Tests.Eval;

public sealed class EvalHarnessTests : IDisposable
{
    private readonly string _resultsDir = Path.Combine(
        Path.GetTempPath(), $"athena-eval-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_resultsDir))
        {
            Directory.Delete(_resultsDir, recursive: true);
        }
    }

    private sealed class FixedMetric(string name, Func<QaCase, double> compute) : IMetric<QaCase>
    {
        public string Name => name;

        public Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
            => Task.FromResult(compute(testCase));
    }

    private sealed class FakeFactory(Func<EvalConfig, IReadOnlyList<IMetric<QaCase>>> metrics)
        : IEvalContextFactory
    {
        public List<EvalConfig> Created { get; } = [];

        public Task<EvalContext> CreateAsync(EvalConfig config, CancellationToken ct = default)
        {
            Created.Add(config);
            return Task.FromResult(new EvalContext(metrics(config), [], []));
        }
    }

    private static readonly IReadOnlyList<QaCase> Cases =
    [
        new("q1", "a1", "A1", [1], IsAnswerable: true),
        new("q2", "a2", "B2", [2], IsAnswerable: true),
        new("q3", "INSUFFICIENT_CONTEXT", "", [], IsAnswerable: false),
    ];

    [Fact]
    public async Task RunQa_AggregatesMeanOverApplicableCases_AndWritesCsv()
    {
        // Recall scores 1, 0, NaN over the three cases -> mean 0.5 over the two applicable.
        var factory = new FakeFactory(_ =>
            [new FixedMetric("ContextRecall@6", c => !c.IsAnswerable ? double.NaN : c.GoldDocId == "A1" ? 1 : 0)]);
        var harness = new EvalHarness(factory, EvalConfig.Of(("chunker", "fixed")), _resultsDir);

        EvalReport report = await harness.RunQaAsync(Cases);

        EvalRun run = Assert.Single(report.Runs);
        Assert.Equal(0.5, run.Aggregates["ContextRecall@6"]);
        Assert.Equal(2, run.Rows.Count); // NaN case produces no row
        Assert.Equal(["qa01", "qa02"], run.Rows.Select(r => r.CaseId));

        Assert.True(File.Exists(report.CsvPath));
        string[] lines = File.ReadAllLines(report.CsvPath);
        Assert.Equal(EvalCsvWriter.Header, lines[0]);
        Assert.Contains(lines, l => l.Contains(",qa,chunker=fixed,qa01,ContextRecall@6,1"));
        Assert.Contains(lines, l => l.Contains($",{EvalCsvWriter.MeanCaseId},ContextRecall@6,0.5"));
    }

    private sealed class CumulativeMetric : IMetric<RecCase>, IAggregatedMetric
    {
        private double _cumulative;

        public string Name => "CatalogueCoverage";

        public Task<double> ComputeAsync(RecCase testCase, CancellationToken ct = default)
            => Task.FromResult(_cumulative += 0.2);

        public double Aggregate(IReadOnlyList<double> caseValues) => caseValues[^1];
    }

    [Fact]
    public async Task RunRecommender_AggregatedMetricUsesItsOwnAggregation_NotTheMean()
    {
        var factory = new RecFakeFactory(() => [new CumulativeMetric()]);
        var harness = new EvalHarness(factory, EvalConfig.Of(("lambda", "0.7")), _resultsDir);

        EvalReport report = await harness.RunRecommenderAsync(
            [new RecCase("s1", ["A"]), new RecCase("s2", ["B"])]);

        // Per-case values 0.2 then 0.4: the mean would be 0.3; coverage must report the final 0.4.
        Assert.Equal(0.4, Assert.Single(report.Runs).Aggregates["CatalogueCoverage"], precision: 10);
    }

    private sealed class RecFakeFactory(Func<IReadOnlyList<IMetric<RecCase>>> metrics) : IEvalContextFactory
    {
        public Task<EvalContext> CreateAsync(EvalConfig config, CancellationToken ct = default)
            => Task.FromResult(new EvalContext([], metrics(), []));
    }

    [Fact]
    public async Task RunAblation_OneContextPerConfig_AllArmsInOneCsv()
    {
        var factory = new FakeFactory(config =>
            [new FixedMetric("ContextRecall@6", _ => config.Get("chunker", "?") == "section" ? 1 : 0)]);
        var harness = new EvalHarness(factory, EvalConfig.Of(("chunker", "fixed")), _resultsDir);
        var grid = new AblationGrid(
            "chunker",
            [EvalConfig.Of(("chunker", "fixed")), EvalConfig.Of(("chunker", "section"))],
            Cases,
            RecCases: []);

        EvalReport report = await harness.RunAblationAsync(grid);

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(2, report.Runs.Count);
        Assert.Equal(0d, report.Runs[0].Aggregates["ContextRecall@6"]);
        Assert.Equal(1d, report.Runs[1].Aggregates["ContextRecall@6"]);

        string csv = File.ReadAllText(report.CsvPath);
        Assert.Contains("ablation-chunker", Path.GetFileName(report.CsvPath));
        Assert.Contains("chunker=fixed", csv);
        Assert.Contains("chunker=section", csv);
    }
}
