using Athena.Retrieval;

namespace Athena.Tests.Retrieval;

public sealed class ReciprocalRankFusionTests
{
    private static Passage P(string chunkId, string docId = "DOC", double score = 0)
        => new(chunkId, docId, $"Title of {docId}", PageNumber: 1, $"Text of {chunkId}", score);

    [Fact]
    public void Fuse_TwoLists_MatchesHandComputedScores()
    {
        // k=60. dense = [A, B, C], lexical = [A, C, D]:
        //   A: 1/61 + 1/61 = 0.0327869
        //   C: 1/63 + 1/62 = 0.0320020
        //   B: 1/62       = 0.0161290
        //   D: 1/63       = 0.0158730
        var fusion = new ReciprocalRankFusion(k: 60);

        var fused = fusion.Fuse(
        [
            [P("A"), P("B"), P("C")],
            [P("A"), P("C"), P("D")],
        ]);

        Assert.Equal(["A", "C", "B", "D"], fused.Select(p => p.ChunkId));
        Assert.Equal(1.0 / 61 + 1.0 / 61, fused[0].Score, precision: 10);
        Assert.Equal(1.0 / 63 + 1.0 / 62, fused[1].Score, precision: 10);
        Assert.Equal(1.0 / 62, fused[2].Score, precision: 10);
        Assert.Equal(1.0 / 63, fused[3].Score, precision: 10);
    }

    [Fact]
    public void Fuse_RespectsConfiguredK()
    {
        // k=1: single list [A, B] scores A = 1/(1+1) = 0.5, B = 1/(1+2) = 0.3333...
        var fusion = new ReciprocalRankFusion(k: 1);

        var fused = fusion.Fuse([[P("A"), P("B")]]);

        Assert.Equal(0.5, fused[0].Score, precision: 10);
        Assert.Equal(1.0 / 3, fused[1].Score, precision: 10);
    }

    [Fact]
    public void Fuse_ConsensusBeatsOneHighRank()
    {
        // X is #1 in one list only; Y is #2 in both. Y: 2/62 = 0.03226 > X: 1/61 = 0.01639.
        // This is the point of fusion — agreement across retrievers outweighs one top spot.
        var fusion = new ReciprocalRankFusion();

        var fused = fusion.Fuse(
        [
            [P("X"), P("Y")],
            [P("Z"), P("Y")],
        ]);

        Assert.Equal("Y", fused[0].ChunkId);
        Assert.Equal(2.0 / 62, fused[0].Score, precision: 10);
    }

    [Fact]
    public void Fuse_EqualScores_TieBreakDeterministicByChunkId()
    {
        // dense = [B, A], lexical = [A, B]: both score 1/61 + 1/62. Ordinal ChunkId decides.
        var fusion = new ReciprocalRankFusion();

        var fused = fusion.Fuse(
        [
            [P("B"), P("A")],
            [P("A"), P("B")],
        ]);

        Assert.Equal(["A", "B"], fused.Select(p => p.ChunkId));
        Assert.Equal(fused[0].Score, fused[1].Score, precision: 12);
    }

    [Fact]
    public void Fuse_KeepsMetadataFromFirstListSeen_AndReplacesScore()
    {
        var fusion = new ReciprocalRankFusion();
        var dense = new Passage("A", "D1", "Dense title", 7, "dense text", 0.93);
        var lexical = new Passage("A", "D1", "Lexical title", 7, "lexical text", 14.2);

        var fused = fusion.Fuse([[dense], [lexical]]);

        Passage result = Assert.Single(fused);
        Assert.Equal("Dense title", result.Title);
        Assert.Equal("dense text", result.Text);
        Assert.Equal(7, result.PageNumber);
        // Raw cosine/BM25 scores must not leak through fusion — the fused score is rank-based.
        Assert.Equal(2.0 / 61, result.Score, precision: 10);
    }

    [Fact]
    public void Fuse_EmptyInput_ReturnsEmpty()
    {
        var fusion = new ReciprocalRankFusion();

        Assert.Empty(fusion.Fuse([]));
        Assert.Empty(fusion.Fuse([[], []]));
    }

    [Fact]
    public void Constructor_NegativeK_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReciprocalRankFusion(k: -1));
    }
}
