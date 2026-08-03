using Athena.Core.Records;
using Athena.Ingestion.Chunking;
using Athena.Ingestion.Extraction;

namespace Athena.Tests.Chunking;

public sealed class SectionAwareChunkerTests
{
    private static SectionAwareChunker Chunker(int maxSection = 10, int window = 8, int overlap = 2)
        => new(new WordTokenCounter(), maxSection, window, overlap);

    [Fact]
    public void Splits_on_di_labelled_headings()
    {
        var doc = ChunkerTestSupport.Document(paragraphs:
        [
            new PageParagraph(1, "Principle 1", ParagraphKind.SectionHeading),
            new PageParagraph(1, "banks should be resilient", ParagraphKind.Body),
            new PageParagraph(2, "Principle 2", ParagraphKind.SectionHeading),
            new PageParagraph(2, "boards should govern", ParagraphKind.Body),
        ]);

        IReadOnlyList<ChunkRecord> chunks = Chunker().Chunk(doc);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Principle 1", chunks[0].Section);
        Assert.Equal("Principle 2", chunks[1].Section);
        // Heading text is prefixed into the chunk so retrieval sees the structural context.
        Assert.StartsWith("Principle 1", chunks[0].Text);
        Assert.Equal(1, chunks[0].PageNumber);
        Assert.Equal(2, chunks[1].PageNumber);
    }

    [Fact]
    public void Oversized_sections_are_subdivided_and_keep_their_heading()
    {
        var doc = ChunkerTestSupport.Document(paragraphs:
        [
            new PageParagraph(3, "Long Section", ParagraphKind.SectionHeading),
            new PageParagraph(3, ChunkerTestSupport.Words(25), ParagraphKind.Body),
        ]);

        IReadOnlyList<ChunkRecord> chunks = Chunker(maxSection: 10, window: 8, overlap: 2).Chunk(doc);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.Equal("Long Section", c.Section));
        Assert.All(chunks, c => Assert.StartsWith("Long Section", c.Text));
        Assert.All(chunks, c => Assert.Equal(3, c.PageNumber));
    }

    [Fact]
    public void Preamble_before_first_heading_is_its_own_section()
    {
        var doc = ChunkerTestSupport.Document(paragraphs:
        [
            new PageParagraph(1, "abstract text before any heading", ParagraphKind.Body),
            new PageParagraph(2, "Introduction", ParagraphKind.SectionHeading),
            new PageParagraph(2, "intro body", ParagraphKind.Body),
        ]);

        IReadOnlyList<ChunkRecord> chunks = Chunker().Chunk(doc);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(string.Empty, chunks[0].Section);
        Assert.Equal(1, chunks[0].PageNumber);
        Assert.Equal("Introduction", chunks[1].Section);
    }

    [Fact]
    public void Title_paragraph_starts_a_section_like_a_heading()
    {
        var doc = ChunkerTestSupport.Document(paragraphs:
        [
            new PageParagraph(1, "The Paper Title", ParagraphKind.Title),
            new PageParagraph(1, "abstract body", ParagraphKind.Body),
        ]);

        ChunkRecord chunk = Assert.Single(Chunker().Chunk(doc));
        Assert.Equal("The Paper Title", chunk.Section);
    }

    [Fact]
    public void Footnotes_are_body_text_and_ocr_pages_yield_ocr_prose()
    {
        var doc = ChunkerTestSupport.Document(
            paragraphs:
            [
                new PageParagraph(1, "Heading", ParagraphKind.SectionHeading),
                new PageParagraph(1, "body", ParagraphKind.Body),
                new PageParagraph(1, "a footnote", ParagraphKind.Footnote),
            ],
            pageKinds: new Dictionary<int, ChunkKind> { [1] = ChunkKind.OcrProse });

        ChunkRecord chunk = Assert.Single(Chunker().Chunk(doc));

        Assert.Contains("a footnote", chunk.Text);
        Assert.Equal(ChunkKind.OcrProse, chunk.Kind);
    }

    [Fact]
    public void Tables_become_their_own_chunks_after_sections()
    {
        var doc = ChunkerTestSupport.Document(
            paragraphs:
            [
                new PageParagraph(1, "Heading", ParagraphKind.SectionHeading),
                new PageParagraph(1, "body", ParagraphKind.Body),
            ],
            tables: [new PageTable(1, "| a |\n| --- |\n| b |")]);

        IReadOnlyList<ChunkRecord> chunks = Chunker().Chunk(doc);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(ChunkKind.Table, chunks[1].Kind);
    }
}
