namespace Athena.Eval;

/// <summary>One (case, metric) measurement under one configuration.</summary>
public sealed record EvalRow(string CaseId, string MetricName, double Value);

/// <summary>All measurements for one configuration, plus per-metric means.</summary>
public sealed record EvalRun(
    EvalConfig Config,
    IReadOnlyList<EvalRow> Rows,
    IReadOnlyDictionary<string, double> Aggregates);

/// <summary>
/// The outcome of one harness invocation (brief §11). <see cref="CsvPath"/> points at the
/// timestamped file under eval/results/ — committed, not screenshotted.
/// </summary>
public sealed record EvalReport(
    string Kind,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<EvalRun> Runs,
    string CsvPath);
