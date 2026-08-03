using System.Globalization;
using System.Text;

namespace Athena.Eval;

/// <summary>
/// Pivots an ablation report into one metric-by-arm Markdown table — the four §11.3
/// interpretation paragraphs get written under numbers that sit side by side, not in
/// sequential per-arm blocks. Columns are labelled by the config keys that actually VARY
/// across the arms; the constant keys are stated once above the table.
/// </summary>
internal static class AblationReportFormatter
{
    internal static string BuildMatrix(EvalReport report)
    {
        IReadOnlyList<EvalRun> runs = report.Runs;
        var allKeys = runs.SelectMany(r => r.Config.Values.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        var varying = allKeys
            .Where(k => runs.Select(r => r.Config.Get(k, "")).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();
        var constant = allKeys.Except(varying, StringComparer.Ordinal).ToList();

        string ColumnLabel(EvalRun run) => varying.Count == 0
            ? run.Config.Describe()
            : string.Join(";", varying.Select(k => run.Config.Get(k, "?")));

        var sb = new StringBuilder();
        sb.AppendLine($"# Ablation: {report.Kind} ({report.StartedAtUtc:yyyy-MM-dd HH:mm:ss}Z)");
        sb.AppendLine();
        if (constant.Count > 0)
        {
            sb.AppendLine("Held constant: " + string.Join("; ",
                constant.Select(k => $"{k}={runs[0].Config.Get(k, "?")}")) + ".");
            sb.AppendLine();
        }

        sb.AppendLine("| Metric | " + string.Join(" | ", runs.Select(ColumnLabel)) + " |");
        sb.AppendLine("|---" + string.Concat(Enumerable.Repeat("|---", runs.Count)) + "|");

        var metricNames = runs
            .SelectMany(r => r.Aggregates.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (string metric in metricNames)
        {
            var cells = runs.Select(r => r.Aggregates.TryGetValue(metric, out double v)
                ? v.ToString("0.000", CultureInfo.InvariantCulture)
                : "—");
            sb.AppendLine($"| {metric} | {string.Join(" | ", cells)} |");
        }

        return sb.ToString();
    }
}
