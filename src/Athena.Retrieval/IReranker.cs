namespace Athena.Retrieval;

/// <summary>
/// Prompt-function reranking (§7): score each candidate 0–10 for relevance to the query,
/// keep the topK.
/// </summary>
public interface IReranker
{
    Task<IReadOnlyList<Passage>> RerankAsync(string query, IReadOnlyList<Passage> candidates,
                                             int topK, CancellationToken ct = default);
}
