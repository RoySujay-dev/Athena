using Athena.Eval;
using Athena.Eval.Metrics;
using Athena.Retrieval;

namespace Athena.Tests.Eval;

/// <summary>
/// The answer-quality metrics against canned QaAnswers — no model, no network. The LLM-judge
/// halves of Faithfulness/AnswerCorrectness are covered by their parse helpers.
/// </summary>
public sealed class QaAnswerMetricTests
{
    private sealed class FakeAnswers(string answer, int violations = 0) : IQaAnswerSource
    {
        public Task<QaAnswer> GetAsync(string question, CancellationToken ct = default)
            => Task.FromResult(new QaAnswer(answer,
                [new Passage("c1", "A1", "Title", 1, "text", 1.0)], violations));
    }

    private static readonly QaCase Answerable = new(
        "What does d516 require?", "Banks must set tolerances.", "A1", [11], IsAnswerable: true);

    private static readonly QaCase Unanswerable = new(
        "Who won the 2019 Cricket World Cup?", "", "", [], IsAnswerable: false);

    [Fact]
    public async Task AbstentionAccuracy_ExactTokenScoresOne_HedgeScoresZero()
    {
        Assert.Equal(1, await new AbstentionAccuracy(new FakeAnswers("INSUFFICIENT_CONTEXT"))
            .ComputeAsync(Unanswerable));
        Assert.Equal(1, await new AbstentionAccuracy(new FakeAnswers("  INSUFFICIENT_CONTEXT\n"))
            .ComputeAsync(Unanswerable)); // whitespace tolerated
        Assert.Equal(0, await new AbstentionAccuracy(new FakeAnswers("Sorry, INSUFFICIENT_CONTEXT."))
            .ComputeAsync(Unanswerable)); // a hedge IS the failure
        Assert.True(double.IsNaN(await new AbstentionAccuracy(new FakeAnswers("anything"))
            .ComputeAsync(Answerable)));
    }

    [Fact]
    public async Task CitationViolationRate_FlagsAnswersTheGuardStripped()
    {
        Assert.Equal(1, await new CitationViolationRate(new FakeAnswers("answer", violations: 2))
            .ComputeAsync(Answerable));
        Assert.Equal(0, await new CitationViolationRate(new FakeAnswers("answer", violations: 0))
            .ComputeAsync(Answerable));
        Assert.True(double.IsNaN(await new CitationViolationRate(new FakeAnswers("x"))
            .ComputeAsync(Unanswerable)));
    }

    [Fact]
    public void FaithfulnessParse_ClaimRatio_ClampedAndFencesStripped()
    {
        Assert.Equal(0.75, Faithfulness.ParseClaimRatio("""{"total": 4, "supported": 3}"""), precision: 10);
        Assert.Equal(1.0, Faithfulness.ParseClaimRatio("""{"total": 2, "supported": 5}"""));    // clamp
        Assert.True(double.IsNaN(Faithfulness.ParseClaimRatio("""{"total": 0, "supported": 0}""")));
        Assert.Equal(0.5, Faithfulness.ParseClaimRatio("```json\n{\"total\": 2, \"supported\": 1}\n```"), precision: 10);
    }

    [Fact]
    public void CorrectnessParse_ScoreClamped()
    {
        Assert.Equal(0.5, AnswerCorrectness.ParseScore("""{"score": 0.5}"""), precision: 10);
        Assert.Equal(1.0, AnswerCorrectness.ParseScore("""{"score": 3}"""));
        Assert.Equal(0.0, AnswerCorrectness.ParseScore("""{"score": -1}"""));
    }

    [Fact]
    public async Task OcrDelta_ComparesTheTwoCopiesOnTheirOwnChunks()
    {
        static OcrDelta Metric(bool nativeHit, bool scanHit) => new(
            (q, docId, ct) => Task.FromResult<IReadOnlyList<Passage>>(
                (docId == "A1" ? nativeHit : scanHit)
                    ? [new Passage("c", docId!, "T", 11, "t", 1.0)]
                    : []),
            nativeDocId: "A1", scanDocId: "A1-SCAN", scannedPageCount: 12);

        var a1Case = new QaCase("q", "a", "A1", [11], IsAnswerable: true);

        Assert.Equal(0, await Metric(nativeHit: true, scanHit: true).ComputeAsync(a1Case));
        Assert.Equal(-1, await Metric(nativeHit: true, scanHit: false).ComputeAsync(a1Case)); // OCR lost it
        Assert.Equal(1, await Metric(nativeHit: false, scanHit: true).ComputeAsync(a1Case));

        // Not applicable: different gold doc, or gold pages beyond the scanned range.
        Assert.True(double.IsNaN(await Metric(true, true)
            .ComputeAsync(new QaCase("q", "a", "B3", [5], true))));
        Assert.True(double.IsNaN(await Metric(true, true)
            .ComputeAsync(new QaCase("q", "a", "A1", [13], true))));
    }
}
