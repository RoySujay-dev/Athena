using Athena.Core.Records;
using Athena.Recommendation;
using Athena.Retrieval;

namespace Athena.Tests.Recommendation;

public sealed class ReasonGeneratorTests
{
    private static Passage Hit(int page) => new("c" + page, "B4", "RAPTOR", page, "text", 0.5);

    private static Athena.Recommendation.Recommendation Rec(
        SignalBreakdown signals, params string[] topics) =>
        new("B4", "RAPTOR", Reason: string.Empty, topics, Score: 0.7, signals);

    private static ReasonInputs Inputs(
        SignalBreakdown signals,
        string[] topics,
        string[]? contextTopics = null,
        Passage[]? hits = null,
        DocRecord[]? suppressed = null,
        int recentQueryCount = 0,
        string published = "2024-01-15") =>
        new(Rec(signals, topics),
            DateTimeOffset.Parse(published + "T00:00:00Z"),
            hits ?? [],
            suppressed ?? [],
            contextTopics ?? [],
            recentQueryCount);

    [Fact]
    public void ChunkDominant_ProducesTheTargetSentenceShape()
    {
        // Weighted contributions: chunk 0.3·1.0 = 0.30 beats docSim 0.5·0.5 = 0.25 and
        // recency 0.2·0.87 = 0.174 → depth clause leads; profile clause appends.
        string reason = ReasonGenerator.Compose(Inputs(
            new SignalBreakdown(DocSim: 0.5, ChunkAggregate: 1.0, Recency: 0.87, ChunkHits: 4, BestHitRank: 1),
            topics: ["tree-organised retrieval", "rag"],
            contextTopics: ["tree-organised retrieval"],
            hits: [Hit(3), Hit(5), Hit(7), Hit(9)],
            recentQueryCount: 3));

        Assert.Equal(
            "Covers tree-organised retrieval in depth (4 strong matches, pp. 3-9), " +
            "which your last three questions circled around.",
            reason);
    }

    [Fact]
    public void SingleHit_UsesSingularMatchAndSinglePageForm()
    {
        string reason = ReasonGenerator.Compose(Inputs(
            new SignalBreakdown(0.1, 1.0, 0.5, ChunkHits: 1, BestHitRank: 2),
            topics: ["graph rag"],
            hits: [Hit(4)]));

        Assert.StartsWith("Covers graph rag in depth (1 strong match, p. 4).", reason);
    }

    [Fact]
    public void DocSimDominant_WithOverlap_NamesTheSharedTopics()
    {
        // docSim 0.5·0.9 = 0.45 beats chunk 0.3·0.2 = 0.06 and recency 0.2·0.5 = 0.10.
        string reason = ReasonGenerator.Compose(Inputs(
            new SignalBreakdown(0.9, 0.2, 0.5, ChunkHits: 1, BestHitRank: 5),
            topics: ["rag evaluation", "benchmarks", "unrelated"],
            contextTopics: ["benchmarks", "rag evaluation"],
            hits: [Hit(2)]));

        Assert.Equal("Strong topical overlap on rag evaluation, benchmarks.", reason);
    }

    [Fact]
    public void DocSimDominant_NoOverlap_FallsBackToOwnTopic()
    {
        string reason = ReasonGenerator.Compose(Inputs(
            new SignalBreakdown(0.9, 0.0, 0.5, 0, 0),
            topics: ["dense retrieval"]));

        Assert.Equal("Closely related to dense retrieval.", reason);
    }

    [Fact]
    public void RecencyDominant_NamesTheMonthAndYear()
    {
        // No chunk hits; docSim 0.5·0.1 = 0.05 < recency 0.2·1.0 = 0.20.
        string reason = ReasonGenerator.Compose(Inputs(
            new SignalBreakdown(0.1, 0.0, 1.0, 0, 0),
            topics: ["graph rag"],
            published: "2025-11-03"));

        Assert.Equal("A recent take on graph rag (published Nov 2025).", reason);
    }

    [Fact]
    public void SuppressedSibling_AppendsTheSupersedesClause()
    {
        var draft = new DocRecord
        {
            DocId = "A2",
            Title = "Consultative draft",
            PublishedOn = DateTimeOffset.Parse("2020-08-06T00:00:00Z"),
        };

        string reason = ReasonGenerator.Compose(Inputs(
            new SignalBreakdown(0.9, 0.0, 0.5, 0, 0),
            topics: ["operational resilience"],
            suppressed: [draft]));

        Assert.EndsWith(". Supersedes A2, Aug 2020.", reason);
    }

    [Fact]
    public void WithReason_AttachesTheComposedReason_AndKeepsSignals()
    {
        var signals = new SignalBreakdown(0.9, 0.0, 0.5, 0, 0);

        var rec = ReasonGenerator.WithReason(Inputs(signals, topics: ["dense retrieval"]));

        Assert.Equal("Closely related to dense retrieval.", rec.Reason);
        Assert.Equal(signals, rec.Signals); // breakdown stays on the record for verification
    }

    [Fact]
    public void FormatBreakdown_RendersEverySignalWithItsWeight()
    {
        string line = ReasonGenerator.FormatBreakdown(
            new SignalBreakdown(0.823, 1.0, 0.867, ChunkHits: 4, BestHitRank: 1));

        Assert.Equal("docSim=0.823*0.5 chunkAgg=1.000*0.3 recency=0.867*0.2 hits=4 bestRank=1", line);
    }
}
