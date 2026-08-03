using Athena.Recommendation;

namespace Athena.Tests.Recommendation;

public sealed class SessionInterestProfileStoreTests
{
    private static readonly ReadOnlyMemory<float> X = new float[] { 1f, 0f };
    private static readonly ReadOnlyMemory<float> Y = new float[] { 0f, 1f };

    private readonly SessionInterestProfileStore _store = new();

    [Fact]
    public async Task FirstUpdate_SeedsTheProfileWithTheQueryVector()
    {
        ReadOnlyMemory<float> profile = await _store.UpdateAsync("s1", X);

        Assert.Equal(new[] { 1f, 0f }, profile.ToArray());
    }

    [Fact]
    public async Task SecondUpdate_AppliesTheDecayFormula()
    {
        // profile = 0.8*(1,0) + 0.2*(0,1) = (0.8, 0.2), hand-computed componentwise.
        await _store.UpdateAsync("s1", X);
        ReadOnlyMemory<float> profile = await _store.UpdateAsync("s1", Y);

        Assert.Equal(0.8f, profile.Span[0], precision: 6);
        Assert.Equal(0.2f, profile.Span[1], precision: 6);
    }

    [Fact]
    public async Task Sessions_AreIsolated()
    {
        await _store.UpdateAsync("s1", X);
        await _store.MarkSurfacedAsync("s1", ["A1"]);

        Assert.Null(await _store.GetAsync("s2"));
        Assert.Empty(await _store.GetAlreadySurfacedAsync("s2"));
        Assert.NotNull(await _store.GetAsync("s1"));
    }

    [Fact]
    public async Task UnknownSession_ReturnsNullProfileAndEmptySets()
    {
        Assert.Null(await _store.GetAsync("nope"));
        Assert.Empty(await _store.GetAlreadySurfacedAsync("nope"));
        InterestSnapshot snapshot = await _store.GetSnapshotAsync("nope");
        Assert.Null(snapshot.Profile);
        Assert.Empty(snapshot.RecentQueries);
    }

    [Fact]
    public async Task MarkSurfaced_Accumulates_AndIsReadBack()
    {
        await _store.MarkSurfacedAsync("s1", ["A1", "B2"]);
        await _store.MarkSurfacedAsync("s1", ["B2", "C4"]);

        IReadOnlySet<string> surfaced = await _store.GetAlreadySurfacedAsync("s1");
        Assert.Equal(3, surfaced.Count);
        Assert.Contains("C4", surfaced);
    }

    [Fact]
    public async Task RecentQueries_KeepNewestFirst_AndEvictBeyondTheWindow()
    {
        // Window is 5: push 7 distinct vectors, expect the first two evicted.
        for (int i = 0; i < 7; i++)
        {
            await _store.UpdateAsync("s1", new float[] { i, 1f });
        }

        InterestSnapshot snapshot = await _store.GetSnapshotAsync("s1");
        Assert.Equal(5, snapshot.RecentQueries.Count);
        Assert.Equal(6f, snapshot.RecentQueries[0].Span[0]); // newest first
        Assert.Equal(2f, snapshot.RecentQueries[^1].Span[0]); // 0 and 1 evicted
    }

    [Fact]
    public async Task MismatchedVectorLength_Throws()
    {
        await _store.UpdateAsync("s1", X);
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.UpdateAsync("s1", new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public async Task ProfileAffinity_MaxOverRecentQueries_BeatsTheBlurredMean()
    {
        // The §9.4 crossover: turns on cluster A (x-axis) then cluster C (y-axis). The decayed
        // mean points between the clusters — cosine ~0.7 to each — but max-over-recents scores
        // a C candidate against the actual C query: 1.0.
        await _store.UpdateAsync("s1", X);
        await _store.UpdateAsync("s1", Y);
        InterestSnapshot snapshot = await _store.GetSnapshotAsync("s1");

        double affinity = ProfileAffinity.Score(Y, snapshot);
        double meanAffinity = Athena.Core.VectorMath.Cosine(Y, snapshot.Profile!.Value);

        Assert.Equal(1.0, affinity, precision: 6);
        Assert.True(meanAffinity < 0.5, $"mean cosine was {meanAffinity}"); // (0.8,0.2) is A-dominated
    }

    [Fact]
    public async Task ProfileAffinity_NoRecentQueries_FallsBackToProfile_ThenZero()
    {
        // Snapshot with a profile but empty recents (restored-session shape) — build by hand.
        var restored = new InterestSnapshot(X, [], new HashSet<string>());
        Assert.Equal(1.0, ProfileAffinity.Score(X, restored), precision: 6);

        InterestSnapshot empty = await _store.GetSnapshotAsync("fresh");
        Assert.Equal(0, ProfileAffinity.Score(X, empty));
    }
}
