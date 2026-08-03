namespace Athena.Retrieval;

/// <summary>
/// BM25 retrieval over the SAME chunks the dense retriever sees (§7). Not ceremony: an analyst
/// searching "d516 Principle 6" or "nDCG" is issuing a lexical query, and dense retrieval on
/// short exact identifiers is unreliable.
/// </summary>
public interface ILexicalRetriever
{
    /// <param name="docId">When set, restricts the search to chunks of that document.</param>
    Task<IReadOnlyList<Passage>> SearchAsync(string query, int topK, string? docId = null,
                                             CancellationToken ct = default);
}
