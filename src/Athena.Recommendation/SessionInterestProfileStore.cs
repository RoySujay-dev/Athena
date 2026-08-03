using System.Collections.Concurrent;

namespace Athena.Recommendation;

/// <summary>
/// In-memory per-session interest state, keyed by sessionId (matching the in-memory vector
/// store: both rebuild per process). Register SCOPED in the web host; nothing here is static
/// and no state exists outside the per-session entries.
///
/// WHY the recent-query window exists (drift, §9.4 / PLAN §2.8): decay 0.8 has a half-life of
/// ln2/ln(1/0.8) ≈ 3.1 turns, and ~10 turns to drop below ~10% after a topic switch. During
/// that crossover the decayed mean of two unrelated clusters points into the empty space
/// BETWEEN them — a vector about neither topic. Keeping the last k raw query vectors lets the
/// recommender score candidates by MAX similarity across them (multi-vector profile), which
/// stays sharp on both the old and the new interest instead of blurring them together.
/// </summary>
public sealed class SessionInterestProfileStore : IInterestProfileStore
{
    // k = 5: comfortably covers the ~3-turn half-life window where the mean is least
    // trustworthy, without letting a long session's stale interests linger forever.
    private const int RecentQueryWindow = 5;

    private sealed class SessionState
    {
        public float[]? Profile;
        public readonly LinkedList<ReadOnlyMemory<float>> RecentQueries = new();
        public readonly HashSet<string> Surfaced = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, SessionState> _sessions =
        new(StringComparer.Ordinal);

    public Task<ReadOnlyMemory<float>> UpdateAsync(string sessionId, ReadOnlyMemory<float> queryVector,
                                                   double decay = 0.8, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(decay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decay, 1.0);

        SessionState state = _sessions.GetOrAdd(sessionId, _ => new SessionState());
        lock (state)
        {
            // First observation seeds the profile outright. Decaying from a zero vector would
            // give the opening query a 0.2 weight against nothing — the first turns of a
            // session would be systematically underweighted for no reason.
            if (state.Profile is null)
            {
                state.Profile = queryVector.ToArray();
            }
            else
            {
                float[] profile = state.Profile;
                ReadOnlySpan<float> q = queryVector.Span;
                if (q.Length != profile.Length)
                {
                    throw new ArgumentException(
                        $"Query vector length {q.Length} does not match profile length {profile.Length}.",
                        nameof(queryVector));
                }

                for (int i = 0; i < profile.Length; i++)
                {
                    profile[i] = (float)(decay * profile[i] + (1 - decay) * q[i]);
                }
            }

            state.RecentQueries.AddFirst(queryVector); // newest first
            while (state.RecentQueries.Count > RecentQueryWindow)
            {
                state.RecentQueries.RemoveLast();
            }

            return Task.FromResult<ReadOnlyMemory<float>>(state.Profile.ToArray());
        }
    }

    public Task<ReadOnlyMemory<float>?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out SessionState? state))
        {
            return Task.FromResult<ReadOnlyMemory<float>?>(null);
        }

        lock (state)
        {
            // Explicit branch typing: the float[]→ReadOnlyMemory implicit conversion accepts a
            // null ARRAY and yields a NON-null empty memory (a ternary producing float[]? hits
            // it too), which would report a phantom empty profile for a session that has only
            // marked documents surfaced. Each branch must convert to the nullable separately.
            return Task.FromResult(state.Profile is { } profile
                ? (ReadOnlyMemory<float>?)new ReadOnlyMemory<float>(profile.ToArray())
                : null);
        }
    }

    public Task<IReadOnlySet<string>> GetAlreadySurfacedAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out SessionState? state))
        {
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
        }

        lock (state)
        {
            return Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(state.Surfaced, StringComparer.Ordinal));
        }
    }

    public Task MarkSurfacedAsync(string sessionId, IEnumerable<string> docIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SessionState state = _sessions.GetOrAdd(sessionId, _ => new SessionState());
        lock (state)
        {
            foreach (string docId in docIds)
            {
                state.Surfaced.Add(docId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<InterestSnapshot> GetSnapshotAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out SessionState? state))
        {
            return Task.FromResult(new InterestSnapshot(
                null, [], new HashSet<string>(StringComparer.Ordinal)));
        }

        lock (state)
        {
            // Same null-conversion trap as GetAsync: keep a null profile genuinely null.
            return Task.FromResult(new InterestSnapshot(
                state.Profile is { } profile
                    ? (ReadOnlyMemory<float>?)new ReadOnlyMemory<float>(profile.ToArray())
                    : null,
                state.RecentQueries.ToList(),
                new HashSet<string>(state.Surfaced, StringComparer.Ordinal)));
        }
    }
}
