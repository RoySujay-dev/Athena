namespace Athena.Retrieval;

/// <summary>Embedding-based chunk retrieval (§7).</summary>
public interface IDenseRetriever
{
    /// <param name="docId">When set, restricts the search to chunks of that document.</param>
    Task<IReadOnlyList<Passage>> SearchAsync(string query, int topK, string? docId = null,
                                             CancellationToken ct = default);
}
