using Athena.Retrieval;

namespace Athena.Tests.Retrieval;

public sealed class DiversityCapTests
{
    private static Passage P(string docId, int rank)
        => new($"{docId}-{rank}", docId, docId, PageNumber: rank, $"text {rank}", Score: 100 - rank);

    [Fact]
    public void Apply_CapsDominantDocument_AndPromotesRunnerUp()
    {
        // B2 owns ranks 1-6; B1 sits at 7-8 — the live failure shape.
        IReadOnlyList<Passage> ranked =
            [P("B2", 1), P("B2", 2), P("B2", 3), P("B2", 4), P("B2", 5), P("B2", 6), P("B1", 7), P("B1", 8)];

        IReadOnlyList<Passage> selected = DiversityCap.Apply(ranked, topK: 6, maxPerDoc: 4);

        Assert.Equal(["B2-1", "B2-2", "B2-3", "B2-4", "B1-7", "B1-8"], selected.Select(p => p.ChunkId));
    }

    [Fact]
    public void Apply_BackfillsFromSkipped_WhenNoOtherDocumentExists()
    {
        // Single-document ranking: the cap must redistribute, never shrink K.
        IReadOnlyList<Passage> ranked =
            [P("B2", 1), P("B2", 2), P("B2", 3), P("B2", 4), P("B2", 5), P("B2", 6)];

        IReadOnlyList<Passage> selected = DiversityCap.Apply(ranked, topK: 6, maxPerDoc: 4);

        Assert.Equal(6, selected.Count);
        Assert.Equal(["B2-1", "B2-2", "B2-3", "B2-4", "B2-5", "B2-6"], selected.Select(p => p.ChunkId));
    }

    [Fact]
    public void Apply_PreservesRankOrder_WhenNoDocumentHitsTheCap()
    {
        IReadOnlyList<Passage> ranked = [P("B1", 1), P("B2", 2), P("D2", 3), P("B1", 4)];

        IReadOnlyList<Passage> selected = DiversityCap.Apply(ranked, topK: 4, maxPerDoc: 4);

        Assert.Equal(["B1-1", "B2-2", "D2-3", "B1-4"], selected.Select(p => p.ChunkId));
    }

    [Fact]
    public void Apply_StopsAtTopK()
    {
        IReadOnlyList<Passage> ranked = [P("B1", 1), P("B2", 2), P("D2", 3), P("D3", 4)];

        Assert.Equal(2, DiversityCap.Apply(ranked, topK: 2, maxPerDoc: 4).Count);
    }
}
