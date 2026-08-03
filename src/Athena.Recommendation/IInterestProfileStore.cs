namespace Athena.Recommendation;

/// <summary>
/// Everything recommend_for_user needs from one session's history, in one read:
/// the brief's decayed mean, the last-k query vectors (drift handling — candidates are
/// scored by MAX similarity across these, not by similarity to the mean), and the DocIds
/// already surfaced this session (excluded from new lists).
/// </summary>
public sealed record InterestSnapshot(
    ReadOnlyMemory<float>? Profile,
    IReadOnlyList<ReadOnlyMemory<float>> RecentQueries,
    IReadOnlySet<string> SurfacedDocIds);

/// <summary>
/// Per-session interest state (brief §9.4). Scoped per session — NOT a static field, NOT a
/// singleton keyed on nothing; all state is keyed by sessionId. The two *Surfaced*/Snapshot
/// members are README-documented additions to the brief's skeleton: the getter alone cannot
/// record surfacing, and the recommender needs the recent-query window for drift handling.
/// </summary>
public interface IInterestProfileStore
{
    /// <summary>profile &lt;- decay * profile + (1 - decay) * embed(latestQuery), decay = 0.8.</summary>
    Task<ReadOnlyMemory<float>> UpdateAsync(string sessionId, ReadOnlyMemory<float> queryVector,
                                            double decay = 0.8, CancellationToken ct = default);

    Task<ReadOnlyMemory<float>?> GetAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlySet<string>> GetAlreadySurfacedAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Records documents shown to this session so later lists exclude them.</summary>
    Task MarkSurfacedAsync(string sessionId, IEnumerable<string> docIds, CancellationToken ct = default);

    Task<InterestSnapshot> GetSnapshotAsync(string sessionId, CancellationToken ct = default);
}
