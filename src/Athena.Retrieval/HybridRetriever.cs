namespace Athena.Retrieval;

/// <summary>
/// The §7 pipeline: dense top-20 + lexical top-20 → RRF → top-10 → rerank → top-K (default 6).
/// Pure orchestration — every stage is an injected, independently-tested collaborator.
/// </summary>
public sealed class HybridRetriever
{
    // 20 per retriever gives RRF real overlap to reward while staying cheap; the rerank
    // shortlist of 10 bounds the LLM grading prompt. Both are the brief's §7 numbers.
    private const int PerRetrieverTopK = 20;
    private const int RerankShortlist = 10;

    // 4 of 6 slots for the best document: single-doc questions rarely need more than four
    // chunks of one document, and cross-document questions (why did X adopt Y's method?) need
    // the remaining slots kept open for the second document, whose vocabulary the term-dense
    // one otherwise crowds out of the top-K entirely. Trade-off: a genuinely single-document
    // question gives up its 5th/6th-best chunk for two chunks of the runner-up document.
    private const int MaxPassagesPerDocument = 4;

    private readonly IDenseRetriever _dense;
    private readonly ILexicalRetriever _lexical;
    private readonly IRankFusion _fusion;
    private readonly IReranker _reranker;

    public HybridRetriever(IDenseRetriever dense, ILexicalRetriever lexical,
                           IRankFusion fusion, IReranker reranker)
    {
        _dense = dense;
        _lexical = lexical;
        _fusion = fusion;
        _reranker = reranker;
    }

    public async Task<IReadOnlyList<Passage>> RetrieveAsync(string query, int topK = 6,
                                                            string? docId = null,
                                                            CancellationToken ct = default)
    {
        // The two retrievers are independent (embedding API call vs in-memory Lucene), so run
        // them concurrently.
        Task<IReadOnlyList<Passage>> denseTask = _dense.SearchAsync(query, PerRetrieverTopK, docId, ct);
        Task<IReadOnlyList<Passage>> lexicalTask = _lexical.SearchAsync(query, PerRetrieverTopK, docId, ct);
        await Task.WhenAll(denseTask, lexicalTask);

        IReadOnlyList<Passage> fused = _fusion.Fuse([await denseTask, await lexicalTask]);
        IReadOnlyList<Passage> shortlist = fused.Take(RerankShortlist).ToList();

        // Rerank the WHOLE shortlist (not just top-K) so the diversity cap has ranked
        // passages to promote when one document owns the head of the list. A docId-filtered
        // search is single-document by construction — capping there would only shrink K.
        IReadOnlyList<Passage> reranked = await _reranker.RerankAsync(query, shortlist, shortlist.Count, ct);
        return docId is null
            ? DiversityCap.Apply(reranked, topK, MaxPassagesPerDocument)
            : reranked.Take(topK).ToList();
    }
}
