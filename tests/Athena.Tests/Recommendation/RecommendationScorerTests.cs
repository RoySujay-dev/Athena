using Athena.Core.Records;
using Athena.Recommendation;
using Athena.Retrieval;

namespace Athena.Tests.Recommendation;

/// <summary>
/// Each §9.3 signal is isolated with a weight vector that zeroes the other two, so every
/// expected ordering is hand-computable.
/// </summary>
public sealed class RecommendationScorerTests
{
    private static readonly ReadOnlyMemory<float> Query = new float[] { 1f, 0f };
    private static readonly DateTimeOffset AsOf = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DocRecord Doc(string id, double degrees = 0, int chunkCount = 1,
                                 string published = "2026-01-01")
    {
        double radians = degrees * Math.PI / 180.0;
        return new DocRecord
        {
            DocId = id,
            Title = $"Title of {id}",
            Topics = ["topic-a", "topic-b"],
            ChunkCount = chunkCount,
            // AssumeUniversal: recency ages are computed against a UTC AsOf, and the local
            // machine offset must not leak into hand-computed day counts.
            PublishedOn = DateTimeOffset.Parse(published, null,
                System.Globalization.DateTimeStyles.AssumeUniversal),
            Embedding = new[] { (float)Math.Cos(radians), (float)Math.Sin(radians) },
        };
    }

    private static Passage Hit(string docId) =>
        new($"{docId}-chunk-{Guid.NewGuid():N}", docId, $"Title of {docId}", 1, "text", 0.5);

    private static RecommendationScorer Scorer(double w1 = 0, double w2 = 0, double w3 = 0,
                                               int maxHitsPerDoc = 5)
        => new(new RecommendationScorerOptions
        {
            DocSimWeight = w1,
            ChunkAggregateWeight = w2,
            RecencyWeight = w3,
            MaxHitsPerDoc = maxHitsPerDoc,
        });

    [Fact]
    public void DocSimSignal_OrdersByQueryCosine()
    {
        var scorer = Scorer(w1: 1);
        var ranked = scorer.Score("q", Query,
            [Doc("FAR", degrees: 80), Doc("NEAR", degrees: 5), Doc("MID", degrees: 40)],
            chunkHits: [], AsOf);

        Assert.Equal(["NEAR", "MID", "FAR"], ranked.Select(r => r.DocId));
    }

    [Fact]
    public void ChunkAggregate_TwoMidRankHitsBeatOneTopHit()
    {
        // Rank-weighted, equal chunkCounts:
        //   D1: 1/61          = 0.016393
        //   D2: 1/62 + 1/63   = 0.032002  → depth of coverage beats one lucky chunk
        var scorer = Scorer(w2: 1);
        var ranked = scorer.Score("q", Query, [Doc("D1"), Doc("D2")],
            [Hit("D1"), Hit("D2"), Hit("D2")], AsOf);

        Assert.Equal(["D2", "D1"], ranked.Select(r => r.DocId));
        Assert.Equal(1.0, ranked[0].Signals.ChunkAggregate); // max-normalised winner
    }

    [Fact]
    public void ChunkAggregate_HitsBeyondTheCapAreNotCounted()
    {
        var scorer = Scorer(w2: 1, maxHitsPerDoc: 2);
        var ranked = scorer.Score("q", Query, [Doc("D1")],
            [Hit("D1"), Hit("D1"), Hit("D1"), Hit("D1"), Hit("D1")], AsOf);

        Assert.Equal(2, ranked[0].Signals.ChunkHits);
        Assert.Equal(1, ranked[0].Signals.BestHitRank);
    }

    [Fact]
    public void ChunkAggregate_SqrtLengthNormalisation_StopsTheLongDocWinningOnBulk()
    {
        // Both docs hit once at adjacent ranks; LONG has 9x the chunks. Raw counts would tie
        // them; sqrt-normalisation divides LONG's aggregate by 3, so SHORT wins.
        var scorer = Scorer(w2: 1);
        var ranked = scorer.Score("q", Query,
            [Doc("LONG", chunkCount: 9), Doc("SHORT", chunkCount: 1)],
            [Hit("LONG"), Hit("SHORT")], AsOf);

        Assert.Equal(["SHORT", "LONG"], ranked.Select(r => r.DocId));
    }

    [Fact]
    public void Recency_FreshDocIsOne_AncientDocApproachesTheFloor()
    {
        var scorer = Scorer(w3: 1);
        var ranked = scorer.Score("q", Query,
            [Doc("FRESH", published: "2026-01-01"), Doc("ANCIENT", published: "1990-01-01")],
            chunkHits: [], AsOf);

        Assert.Equal(["FRESH", "ANCIENT"], ranked.Select(r => r.DocId));
        Assert.Equal(1.0, ranked[0].Signals.Recency, precision: 6);
        // ~13,000 days at tau=1000: exp term ≈ 2e-6 — the floor is all that remains.
        Assert.Equal(0.3, ranked[1].Signals.Recency, precision: 3);
    }

    [Fact]
    public void Recency_DraftVsFinalGap_IsANudgeNotADeletion()
    {
        // A2-draft is ~210 days older than A1-final. tau=1000:
        //   final:  0.3 + 0.7·exp(0)        = 1.000
        //   draft:  0.3 + 0.7·exp(-210/1000) = 0.3 + 0.7·0.81058 = 0.86741
        var scorer = Scorer(w3: 1);
        var ranked = scorer.Score("q", Query,
            [Doc("DRAFT", published: "2025-06-05"), Doc("FINAL", published: "2026-01-01")],
            chunkHits: [], AsOf);

        Assert.Equal("FINAL", ranked[0].DocId);
        Assert.Equal(0.86741, ranked[1].Signals.Recency, precision: 4);
    }

    [Fact]
    public void Breakdown_CarriesEverySignal_AndReasonIsLeftToPresentation()
    {
        var scorer = new RecommendationScorer();
        var ranked = scorer.Score("q", Query, [Doc("D1", degrees: 5)], [Hit("D1")], AsOf);

        Athena.Recommendation.Recommendation top = ranked[0]; // qualified: test namespace ends in ".Recommendation"
        Assert.True(top.Signals.DocSim > 0.99);
        Assert.Equal(1.0, top.Signals.ChunkAggregate);
        Assert.Equal(1.0, top.Signals.Recency, precision: 6);
        Assert.Equal(1, top.Signals.ChunkHits);
        Assert.Equal(string.Empty, top.Reason);
        Assert.Equal(["topic-a", "topic-b"], top.Topics);
    }

    [Fact]
    public void NoChunkHits_ScoresWithoutCrashing_AggregateIsZero()
    {
        var scorer = new RecommendationScorer();
        var ranked = scorer.Score("q", Query, [Doc("D1")], [], AsOf);

        Assert.Equal(0, ranked[0].Signals.ChunkAggregate);
        Assert.Equal(0, ranked[0].Signals.ChunkHits);
    }

    [Fact]
    public void TiedScores_BreakByDocIdOrdinal()
    {
        var scorer = new RecommendationScorer();
        var ranked = scorer.Score("q", Query, [Doc("B"), Doc("A")], [], AsOf);

        Assert.Equal(["A", "B"], ranked.Select(r => r.DocId));
    }
}
