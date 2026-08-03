namespace Athena.Filters;

/// <summary>
/// One citation the grounding guard stripped: the answer cited (Title, page) but no passage
/// retrieved this turn matches it. <paramref name="Question"/> is the answer_question argument
/// when available — it is what makes a violation reproducible when auditing the log.
/// </summary>
public sealed record CitationViolation(
    string FunctionName,
    string CitedTitle,
    int CitedPage,
    string? Question);

/// <summary>
/// Sink for stripped citations (brief §8.1). Part F reports the violation rate from this log,
/// so every strip MUST be recorded — a guard that silently fixes answers destroys the metric.
/// </summary>
public interface ICitationViolationLog
{
    Task RecordAsync(CitationViolation violation, CancellationToken ct = default);
}
