using Athena.Retrieval;

namespace Athena.Eval.Metrics;

/// <summary>
/// Brief §11.1: "fraction of questions where a gold page appears in the retrieved chunks".
/// Per case this is binary — 1 if any retrieved passage comes from the gold document AND
/// starts on a gold page, else 0; the harness's mean over cases gives the fraction.
/// </summary>
public sealed class ContextRecallAtK : IMetric<QaCase>
{
    private readonly QaRetrievalSource _retrieval;

    public ContextRecallAtK(QaRetrievalSource retrieval, int k)
    {
        _retrieval = retrieval;
        Name = $"ContextRecall@{k}";
    }

    public string Name { get; }

    public async Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
    {
        if (!testCase.IsAnswerable)
        {
            return double.NaN; // recall is undefined without a gold page; abstention metrics own these
        }

        IReadOnlyList<Passage> retrieved = await _retrieval.GetAsync(testCase.Question, ct);
        return retrieved.Any(p => IsGold(testCase, p)) ? 1d : 0d;
    }

    internal static bool IsGold(QaCase testCase, Passage passage)
        // Strict by design: a page from the lineage SIBLING (A2 when gold is A1) does not
        // count. That strictness is what makes draft-vs-final retrieval failures visible
        // instead of silently passing.
        => passage.DocId == testCase.GoldDocId && testCase.GoldPages.Contains(passage.PageNumber);
}
