using Athena.Retrieval;

namespace Athena.Eval.Metrics;

/// <summary>
/// Brief §11.1: "fraction of retrieved chunks that are relevant". Relevance is page-grounded:
/// a chunk is relevant iff it comes from the gold document and starts on a gold page (same
/// predicate as recall, so the two metrics agree on what "relevant" means). This makes the
/// absolute number harsh — most questions have one gold page among six retrieved chunks, so
/// even perfect retrieval rarely exceeds ~0.3 — but it stays comparable ACROSS configurations,
/// which is what an ablation needs.
/// </summary>
public sealed class ContextPrecisionAtK : IMetric<QaCase>
{
    private readonly QaRetrievalSource _retrieval;

    public ContextPrecisionAtK(QaRetrievalSource retrieval, int k)
    {
        _retrieval = retrieval;
        Name = $"ContextPrecision@{k}";
    }

    public string Name { get; }

    public async Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
    {
        if (!testCase.IsAnswerable)
        {
            return double.NaN;
        }

        IReadOnlyList<Passage> retrieved = await _retrieval.GetAsync(testCase.Question, ct);
        if (retrieved.Count == 0)
        {
            return double.NaN; // precision over zero retrieved chunks is undefined, not zero
        }

        return (double)retrieved.Count(p => ContextRecallAtK.IsGold(testCase, p)) / retrieved.Count;
    }
}
