using Athena.Retrieval;

namespace Athena.Eval;

/// <summary>
/// The "RRF hybrid without rerank" ablation arm (brief §11.3 ablation 1): keeps the fused
/// order and truncates. Lives in Eval because it is an experimental control, not a production
/// reranker.
/// </summary>
internal sealed class PassthroughReranker : IReranker
{
    public Task<IReadOnlyList<Passage>> RerankAsync(
        string query, IReadOnlyList<Passage> candidates, int topK, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Passage>>(candidates.Take(topK).ToList());
}
