using System.Globalization;

namespace Athena.Eval;

/// <summary>
/// One flat CSV per harness invocation: timestamped filename, one row per (config, case,
/// metric) plus MEAN rows, with the full configuration on every row. Flat rows rather than a
/// pivoted table so that results from different runs and ablation arms concatenate and group
/// cleanly when analysed later.
/// </summary>
public static class EvalCsvWriter
{
    public const string Header = "timestamp_utc,kind,config,case_id,metric,value";

    /// <summary>Sentinel case id for the per-metric mean over all applicable cases.</summary>
    public const string MeanCaseId = "MEAN";

    public static string Write(
        string resultsDirectory, string kind, DateTimeOffset startedAtUtc, IReadOnlyList<EvalRun> runs)
    {
        Directory.CreateDirectory(resultsDirectory);
        string timestamp = startedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(resultsDirectory, $"{timestamp}-{kind}.csv");

        var lines = new List<string> { Header };
        string stamp = startedAtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (EvalRun run in runs)
        {
            string config = run.Config.Describe();
            foreach (EvalRow row in run.Rows)
            {
                lines.Add(Line(stamp, kind, config, row.CaseId, row.MetricName, row.Value));
            }

            foreach ((string metric, double mean) in run.Aggregates.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                lines.Add(Line(stamp, kind, config, MeanCaseId, metric, mean));
            }
        }

        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Line(
        string stamp, string kind, string config, string caseId, string metric, double value)
        // No field ever contains a comma or quote by construction (config is k=v;k=v, metrics
        // and case ids are identifiers), so no CSV quoting machinery is needed.
        => string.Join(',', stamp, kind, config, caseId, metric,
            value.ToString("0.######", CultureInfo.InvariantCulture));
}
