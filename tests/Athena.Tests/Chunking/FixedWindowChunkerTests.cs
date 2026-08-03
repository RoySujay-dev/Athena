using Athena.Core.Records;
using Athena.Ingestion.Chunking;
using Athena.Ingestion.Extraction;

namespace Athena.Tests.Chunking;

public sealed class FixedWindowChunkerTests
{
    private static FixedWindowChunker Chunker(int window = 10, int overlap = 2)
        => new(new WordTokenCounter(), window, overlap);

    [Fact]
    public void Windows_respect_token_budget_and_overlap()
    {
        var doc = ChunkerTestSupport.Document(
            pages: [new PageText(1, ChunkerTestSupport.Words(26), 1f)]);

        IReadOnlyList<ChunkRecord> chunks = Chunker(window: 10, overlap: 2).Chunk(doc);

        // Step = window - overlap = 8: windows start at w1, w9, w17; the last window reaches
        // the 26th word, so no fourth window is needed.
        Assert.Equal(3, chunks.Count);
        string[] first = chunks[0].Text.Split(' ');
        string[] second = chunks[1].Text.Split(' ');
        Assert.Equal(10, first.Length);
        // 15%-style overlap: the second window re-reads the last two words of the first.
        Assert.Equal(first[^2..], second[..2]);
        Assert.Equal("w9", second[0]);
    }

    [Fact]
    public void Page_number_is_the_page_the_chunk_starts_on()
    {
        var doc = ChunkerTestSupport.Document(
            pages:
            [
                new PageText(1, ChunkerTestSupport.Words(6, "a"), 1f),
                new PageText(2, ChunkerTestSupport.Words(6, "b"), 1f),
            ]);

        IReadOnlyList<ChunkRecord> chunks = Chunker(window: 10, overlap: 2).Chunk(doc);

        // First window spans the page boundary and is attributed to page 1; the second window
        // starts inside page 2.
        Assert.Equal(1, chunks[0].PageNumber);
        Assert.Equal(2, chunks[1].PageNumber);
    }

    [Fact]
    public void Tables_become_single_unsplit_chunks()
    {
        string table = "| h1 | h2 |\n| --- | --- |\n" +
                       string.Concat(Enumerable.Range(1, 50).Select(i => $"| r{i}a | r{i}b |\n"));
        var doc = ChunkerTestSupport.Document(
            pages: [new PageText(1, "some prose", 1f)],
            tables: [new PageTable(1, table)]);

        IReadOnlyList<ChunkRecord> chunks = Chunker(window: 5, overlap: 1).Chunk(doc);

        ChunkRecord tableChunk = Assert.Single(chunks, c => c.Kind == ChunkKind.Table);
        // Never split, however large relative to the window.
        Assert.Equal(table, tableChunk.Text);
        Assert.Equal(1, tableChunk.PageNumber);
    }

    [Fact]
    public void Chunks_on_ocr_classified_pages_are_ocr_prose()
    {
        var doc = ChunkerTestSupport.Document(
            pages:
            [
                new PageText(1, ChunkerTestSupport.Words(10, "a"), 1f),
                new PageText(2, ChunkerTestSupport.Words(10, "b"), 0.5f),
            ],
            pageKinds: new Dictionary<int, ChunkKind> { [1] = ChunkKind.Prose, [2] = ChunkKind.OcrProse });

        IReadOnlyList<ChunkRecord> chunks = Chunker(window: 10, overlap: 0).Chunk(doc);

        Assert.Equal(ChunkKind.Prose, chunks[0].Kind);
        Assert.Equal(ChunkKind.OcrProse, chunks[1].Kind);
    }

    [Fact]
    public void Chunk_ids_are_deterministic_and_sequential()
    {
        var doc = ChunkerTestSupport.Document(
            pages: [new PageText(1, ChunkerTestSupport.Words(26), 1f)],
            tables: [new PageTable(1, "| a |\n| --- |\n| b |")]);

        IReadOnlyList<ChunkRecord> first = Chunker().Chunk(doc);
        IReadOnlyList<ChunkRecord> second = Chunker().Chunk(doc);

        Assert.Equal(first.Select(c => c.ChunkId), second.Select(c => c.ChunkId));
        Assert.Equal(
            Enumerable.Range(0, first.Count).Select(i => $"T1-{i:D4}"),
            first.Select(c => c.ChunkId));
    }

    [Fact]
    public void Metadata_threads_into_every_chunk()
    {
        var doc = ChunkerTestSupport.Document(pages: [new PageText(1, "hello world", 1f)]);

        ChunkRecord chunk = Assert.Single(Chunker().Chunk(doc));

        Assert.Equal("T1", chunk.DocId);
        Assert.Equal("Test Document", chunk.Title);
        Assert.Equal("A", chunk.Cluster);
        Assert.Equal(ChunkerTestSupport.Meta.PublishedOn, chunk.PublishedOn);
    }

    [Fact]
    public void Empty_document_yields_no_chunks()
    {
        Assert.Empty(Chunker().Chunk(ChunkerTestSupport.Document()));
    }
}
