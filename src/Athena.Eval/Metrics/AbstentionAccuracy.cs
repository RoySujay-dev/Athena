namespace Athena.Eval.Metrics;

/// <summary>
/// Correct INSUFFICIENT_CONTEXT rate on the unanswerable cases (§11.1). Exact match, by
/// design: rule 2 of the answer prompt is "no hedge, no partial guess", so "I'm sorry, but
/// INSUFFICIENT_CONTEXT" scores 0 — a hedge IS the failure this metric exists to catch.
/// </summary>
public sealed class AbstentionAccuracy : IMetric<QaCase>
{
    public const string AbstentionToken = "INSUFFICIENT_CONTEXT";

    private readonly IQaAnswerSource _answers;

    public AbstentionAccuracy(IQaAnswerSource answers)
    {
        _answers = answers;
    }

    public string Name => "AbstentionAccuracy";

    public async Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
    {
        if (testCase.IsAnswerable)
        {
            return double.NaN; // answerable cases are graded by the answer-quality metrics
        }

        QaAnswer answer = await _answers.GetAsync(testCase.Question, ct);
        return Score(answer.Answer);
    }

    internal static double Score(string answer)
        => string.Equals(answer.Trim(), AbstentionToken, StringComparison.Ordinal) ? 1 : 0;
}
