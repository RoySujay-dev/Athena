namespace Athena.Retrieval;

/// <summary>
/// Per-document cap over a ranked passage list. A question mentioning another document's
/// vocabulary ("why did the RAG authors choose DPR over BM25") retrieves a top-K wholly owned
/// by the term-dense document (observed live: all six passages from B2, zero from B1, and the
/// answer addressed the wrong paper) — cross-document questions need the evidence set to keep
/// at least a couple of slots open for the second document. Greedy in rank order, so within
/// the cap the reranker's ordering is untouched; if the cap leaves slots unfilled (single-doc
/// corpus, docId-filtered search), the best skipped passages backfill so K never shrinks.
/// Pure function — unit-tested directly.
/// </summary>
public static class DiversityCap
{
    public static IReadOnlyList<Passage> Apply(IReadOnlyList<Passage> ranked, int topK, int maxPerDoc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerDoc, 1);

        var selected = new List<Passage>(topK);
        var skipped = new List<Passage>();
        var perDoc = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Passage passage in ranked)
        {
            if (selected.Count == topK)
            {
                break;
            }

            int count = perDoc.GetValueOrDefault(passage.DocId);
            if (count < maxPerDoc)
            {
                selected.Add(passage);
                perDoc[passage.DocId] = count + 1;
            }
            else
            {
                skipped.Add(passage);
            }
        }

        // Backfill in rank order: the cap redistributes slots, it never surrenders them.
        foreach (Passage passage in skipped)
        {
            if (selected.Count == topK)
            {
                break;
            }

            selected.Add(passage);
        }

        return selected;
    }
}
