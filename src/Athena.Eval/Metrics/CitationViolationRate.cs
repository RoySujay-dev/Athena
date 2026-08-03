namespace Athena.Eval.Metrics;

/// <summary>
/// Fraction of answered cases where the Part C guard stripped at least one citation (§11.1
/// "from the guard log"). The count comes from the REAL GroundingGuardFilter riding on the
/// answer path — a zero here is credible precisely because the guard's induced-failure unit
/// test proves the detector fires.
/// </summary>
public sealed class CitationViolationRate : IMetric<QaCase>
{
    private readonly IQaAnswerSource _answers;

    public CitationViolationRate(IQaAnswerSource answers)
    {
        _answers = answers;
    }

    public string Name => "CitationViolationRate";

    public async Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
    {
        if (!testCase.IsAnswerable)
        {
            return double.NaN; // an abstention carries no citations to violate
        }

        QaAnswer answer = await _answers.GetAsync(testCase.Question, ct);
        return answer.Violations > 0 ? 1 : 0;
    }
}
