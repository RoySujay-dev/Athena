using Athena.Core.Records;
using Athena.Recommendation;
using Microsoft.Extensions.VectorData;
using Rec = Athena.Recommendation.Recommendation;

namespace Athena.Web.Services;

/// <summary>
/// The "Recommended for you" sidebar: the recommend_for_user flow (profile affinity → lineage
/// dedup → MMR → signal-grounded reasons, all in the same Athena.Recommendation pure
/// components) run as a READ-ONLY projection after each turn. It deliberately skips the two
/// side effects of the kernel function: it does not mark results as surfaced and does not
/// exclude already-surfaced docs. A passive sidebar that consumed the exclusion budget would
/// starve the chat's own recommend_for_user ("what else should I read?") of anything to say
/// after two turns on a ~15-document corpus — the exclusion contract belongs to conversational
/// surfacing, while a persistent sidebar is EXPECTED to re-show the standing best picks as the
/// profile drifts.
/// </summary>
public sealed class SidebarRecommender
{
    private readonly IInterestProfileStore _profiles;
    private readonly VectorStoreCollection<string, DocRecord> _docs;
    private readonly INearDuplicateResolver _dedup;
    private readonly IDiversifier _diversifier;
    private readonly string _sessionId;

    public SidebarRecommender(IInterestProfileStore profiles,
                              VectorStoreCollection<string, DocRecord> docs,
                              INearDuplicateResolver dedup, IDiversifier diversifier,
                              string sessionId)
    {
        _profiles = profiles;
        _docs = docs;
        _dedup = dedup;
        _diversifier = diversifier;
        _sessionId = sessionId;
    }

    public async Task<IReadOnlyList<Rec>> RecommendAsync(int topK = 5, CancellationToken ct = default)
    {
        InterestSnapshot snapshot = await _profiles.GetSnapshotAsync(_sessionId, ct);
        if (snapshot.Profile is null && snapshot.RecentQueries.Count == 0)
        {
            return [];
        }

        var candidates = new List<DocRecord>();
        await foreach (DocRecord doc in _docs.GetAsync(_ => true, top: int.MaxValue, cancellationToken: ct))
        {
            // The manufactured scan is an OCR diagnostic artifact, not a recommendable reading.
            if (!doc.DocId.EndsWith("-SCAN", StringComparison.Ordinal))
            {
                candidates.Add(doc);
            }
        }

        // Same ranking the plugin's recommend_for_user uses: ProfileAffinity in the DocSim
        // slot (max over recent query vectors, §9.4), deterministic DocId tie-break.
        var scored = candidates
            .Select(d => new Rec(
                d.DocId, d.Title, Reason: string.Empty, d.Topics,
                Score: ProfileAffinity.Score(d.Embedding, snapshot),
                new SignalBreakdown(
                    DocSim: ProfileAffinity.Score(d.Embedding, snapshot),
                    ChunkAggregate: 0, Recency: 0, ChunkHits: 0, BestHitRank: 0)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.DocId, StringComparer.Ordinal)
            .ToList();

        var byId = candidates.ToDictionary(d => d.DocId, StringComparer.Ordinal);
        var rankedDocs = scored.Select(r => byId[r.DocId]).ToList();
        IReadOnlyList<ResolvedDoc> resolved = _dedup.ResolveWithProvenance(rankedDocs);
        IReadOnlyList<DocRecord> selected = _diversifier.Select(
            snapshot.Profile ?? snapshot.RecentQueries[0],
            resolved.Select(r => r.Doc).ToList(), topK, lambda: 0.7);

        var scoredById = scored.ToDictionary(r => r.DocId, StringComparer.Ordinal);
        var provenanceById = resolved.ToDictionary(r => r.Doc.DocId, StringComparer.Ordinal);
        return selected
            .Select(doc => ReasonGenerator.WithReason(new ReasonInputs(
                scoredById[doc.DocId], doc.PublishedOn, DocChunkHits: [],
                provenanceById[doc.DocId].SuppressedSiblings, ContextTopics: [],
                snapshot.RecentQueries.Count)))
            .ToList();
    }
}
