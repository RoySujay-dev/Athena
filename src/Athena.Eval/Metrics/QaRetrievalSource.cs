using System.Collections.Concurrent;
using Athena.Retrieval;

namespace Athena.Eval.Metrics;

/// <summary>
/// Memoized retrieval shared by every retrieval-based QA metric in one run. The brief's
/// IMetric signature hands each metric only the case, so Recall@6 and Precision@6 would each
/// retrieve for the same question — this dedupes the (paid) embedding and rerank calls and
/// guarantees both metrics grade the SAME retrieved list.
/// </summary>
public sealed class QaRetrievalSource
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<Passage>>> _retrieve;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<Passage>>>> _cache =
        new(StringComparer.Ordinal);

    public QaRetrievalSource(Func<string, CancellationToken, Task<IReadOnlyList<Passage>>> retrieve)
    {
        _retrieve = retrieve;
    }

    /// <summary>First caller's token governs the shared retrieval; harness runs are sequential.</summary>
    public Task<IReadOnlyList<Passage>> GetAsync(string question, CancellationToken ct = default)
        => _cache.GetOrAdd(question,
            q => new Lazy<Task<IReadOnlyList<Passage>>>(() => _retrieve(q, ct))).Value;
}
