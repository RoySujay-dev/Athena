using Athena.Eval;

namespace Athena.Tests.Eval;

public sealed class AblationReportFormatterTests
{
    private static EvalRun Run(Dictionary<string, string> config, Dictionary<string, double> aggregates)
        => new(new EvalConfig(config), [], aggregates);

    [Fact]
    public void BuildMatrix_ColumnsAreTheVaryingAxis_ConstantsStatedOnce()
    {
        var report = new EvalReport("lambda", DateTimeOffset.Parse("2026-07-31T00:00:00Z"),
        [
            Run(new() { ["chunker"] = "fixed", ["lambda"] = "1.0" },
                new() { ["nDCG@5"] = 0.91, ["IntraListDiversity"] = 0.22 }),
            Run(new() { ["chunker"] = "fixed", ["lambda"] = "0.3" },
                new() { ["nDCG@5"] = 0.74, ["IntraListDiversity"] = 0.55 }),
        ], "unused.csv");

        string matrix = AblationReportFormatter.BuildMatrix(report);

        Assert.Contains("Held constant: chunker=fixed.", matrix);
        Assert.Contains("| Metric | 1.0 | 0.3 |", matrix);          // columns = varying axis values
        Assert.Contains("| nDCG@5 | 0.910 | 0.740 |", matrix);       // side-by-side row
        Assert.Contains("| IntraListDiversity | 0.220 | 0.550 |", matrix);
    }

    [Fact]
    public void BuildMatrix_MetricMissingInOneArm_RendersADash()
    {
        var report = new EvalReport("retriever", DateTimeOffset.UtcNow,
        [
            Run(new() { ["retriever"] = "dense" }, new() { ["ContextRecall@6"] = 0.5, ["OcrDelta"] = 0.0 }),
            Run(new() { ["retriever"] = "bm25" }, new() { ["ContextRecall@6"] = 0.6 }),
        ], "unused.csv");

        string matrix = AblationReportFormatter.BuildMatrix(report);

        Assert.Contains("| OcrDelta | 0.000 | — |", matrix);
    }
}
