namespace Athena.Retrieval;

/// <summary>
/// Reciprocal Rank Fusion: score(d) = Σ over lists of 1 / (k + rank_i(d)), rank 1-based.
///
/// Why ranks and not raw scores: cosine similarity (bounded, typically 0.6–0.9 on this corpus)
/// and BM25 (unbounded, query-length dependent) live on incomparable scales, so averaging or
/// blending the raw values produces an ordering that is an artefact of the scales, not of
/// relevance. RRF discards the scales and keeps only each list's ordering, which is the part
/// both retrievers actually agree on the meaning of. (Hard constraint 8 — if a weighted blend
/// is ever proposed, push back here first.)
///
/// Pure function: no I/O, no ambient state — unit-tested with hand-computed scores.
/// </summary>
public sealed class ReciprocalRankFusion : IRankFusion
{
    // k=60 is the value from the original RRF paper (Cormack et al., 2009) and the brief. It
    // damps the gap between neighbouring ranks (1/61 vs 1/62) so that one retriever's #1 cannot
    // swamp broad agreement lower down; smaller k makes fusion top-rank dominated.
    private readonly int _k;

    public ReciprocalRankFusion(int k = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        _k = k;
    }

    public IReadOnlyList<Passage> Fuse(IReadOnlyList<IReadOnlyList<Passage>> rankedLists)
    {
        // Keyed by ChunkId; the first list to mention a chunk supplies the Passage metadata
        // (text, page), later lists only add score. Lists are assumed deduplicated internally.
        var fused = new Dictionary<string, (Passage First, double Score)>();

        foreach (IReadOnlyList<Passage> list in rankedLists)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Passage passage = list[i];
                double contribution = 1.0 / (_k + i + 1); // i is 0-based, rank is i+1
                fused[passage.ChunkId] = fused.TryGetValue(passage.ChunkId, out var existing)
                    ? (existing.First, existing.Score + contribution)
                    : (passage, contribution);
            }
        }

        return fused.Values
            .Select(e => e.First with { Score = e.Score })
            // ChunkId tie-break keeps the ordering deterministic when two chunks earn the
            // exact same reciprocal-rank sum (common with two lists of equal length).
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.ChunkId, StringComparer.Ordinal)
            .ToList();
    }
}
