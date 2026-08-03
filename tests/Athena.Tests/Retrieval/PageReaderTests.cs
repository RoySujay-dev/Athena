using Athena.Core.Records;
using Athena.Retrieval;

namespace Athena.Tests.Retrieval;

public sealed class PageReaderTests
{
    private static ChunkRecord Chunk(string id, int page, int endPage = 0)
        => new() { ChunkId = id, DocId = "B1", PageNumber = page, EndPage = endPage == 0 ? page : endPage };

    [Fact]
    public void SelectPages_ReturnsChunksIntersectingRange_InPageOrder()
    {
        IReadOnlyList<ChunkRecord> chunks =
            [Chunk("c3", 17), Chunk("c1", 15, 16), Chunk("c2", 16), Chunk("c0", 2)];

        IReadOnlyList<ChunkRecord> selected = PageReader.SelectPages(chunks, 16, 16);

        // c1 spans 15-16 so it intersects page 16; c3 (17) and c0 (2) do not.
        Assert.Equal(["c1", "c2"], selected.Select(c => c.ChunkId));
    }

    [Fact]
    public void SelectPages_ZeroToPage_MeansSinglePage()
    {
        IReadOnlyList<ChunkRecord> chunks = [Chunk("c1", 4), Chunk("c2", 5)];

        Assert.Equal(["c1"], PageReader.SelectPages(chunks, 4, 0).Select(c => c.ChunkId));
    }

    [Fact]
    public void SelectPages_CapsSpan_AtMaxPageSpan()
    {
        IReadOnlyList<ChunkRecord> chunks =
            Enumerable.Range(1, 12).Select(p => Chunk($"c{p:00}", p)).ToList();

        IReadOnlyList<ChunkRecord> selected = PageReader.SelectPages(chunks, 1, 12);

        // Pages 1..MaxPageSpan only — a "pages 1-12" ask must not dump the whole document.
        Assert.Equal(PageReader.MaxPageSpan, selected.Count);
        Assert.Equal($"c{PageReader.MaxPageSpan:00}", selected[^1].ChunkId);
    }

    [Fact]
    public void SelectPages_NormalisesNonPositiveFromPage()
    {
        IReadOnlyList<ChunkRecord> chunks = [Chunk("c1", 1), Chunk("c2", 2)];

        Assert.Equal(["c1"], PageReader.SelectPages(chunks, -3, 0).Select(c => c.ChunkId));
    }
}
