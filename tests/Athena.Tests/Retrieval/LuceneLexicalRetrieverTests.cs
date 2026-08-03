using Athena.Core.Records;
using Athena.Retrieval;

namespace Athena.Tests.Retrieval;

/// <summary>
/// Real Lucene index over fabricated chunks — everything is in-memory, so these run as fast
/// unit tests while exercising the actual analyzer + BM25 path, including the exact-identifier
/// queries ("d516", "nDCG") that justify lexical retrieval on this corpus.
/// </summary>
public sealed class LuceneLexicalRetrieverTests : IDisposable
{
    private readonly LuceneLexicalRetriever _retriever = new();

    private static ChunkRecord Chunk(string chunkId, string docId, int page, string text) => new()
    {
        ChunkId = chunkId,
        DocId = docId,
        Title = $"Title of {docId}",
        PageNumber = page,
        Text = text,
    };

    public void Dispose() => _retriever.Dispose();

    [Fact]
    public async Task Search_ExactIdentifierQuery_FindsTheChunkThatContainsIt()
    {
        _retriever.Index(
        [
            Chunk("c1", "A1", 3, "Principle 6 of d516 requires banks to manage model risk."),
            Chunk("c2", "B1", 5, "Dense passage retrieval encodes queries and documents."),
            Chunk("c3", "C1", 8, "We evaluate ranking quality with nDCG and recall metrics."),
        ]);

        IReadOnlyList<Passage> hits = await _retriever.SearchAsync("d516 Principle 6", topK: 3);

        Assert.Equal("c1", hits[0].ChunkId);
        Assert.Equal("A1", hits[0].DocId);
        Assert.Equal(3, hits[0].PageNumber); // page threads through the Lucene stored fields
        Assert.True(hits[0].Score > 0);
    }

    [Fact]
    public async Task Search_IsCaseInsensitive_ViaTheSharedAnalyzer()
    {
        _retriever.Index([Chunk("c1", "C1", 8, "Ranking quality measured with nDCG@10.")]);

        IReadOnlyList<Passage> hits = await _retriever.SearchAsync("NDCG", topK: 1);

        Assert.Single(hits);
    }

    [Fact]
    public async Task Search_DocIdFilter_RestrictsResultsToThatDocument()
    {
        _retriever.Index(
        [
            Chunk("c1", "A1", 1, "model risk management principles"),
            Chunk("c2", "A2", 1, "model risk management principles"),
        ]);

        IReadOnlyList<Passage> hits = await _retriever.SearchAsync("model risk", topK: 5, docId: "A2");

        Passage hit = Assert.Single(hits);
        Assert.Equal("c2", hit.ChunkId);
    }

    [Fact]
    public async Task Search_QueryWithLuceneSyntaxCharacters_DoesNotThrow()
    {
        _retriever.Index([Chunk("c1", "A1", 1, "banking regulation text")]);

        // Hand-built term queries (no QueryParser) — ':' '/' '(' must never cause a parse error.
        IReadOnlyList<Passage> hits = await _retriever.SearchAsync(
            "regulation: (banking/finance)", topK: 5);

        Assert.Single(hits);
    }

    [Fact]
    public async Task Search_NoMatchingTerms_ReturnsEmpty()
    {
        _retriever.Index([Chunk("c1", "A1", 1, "banking regulation text")]);

        Assert.Empty(await _retriever.SearchAsync("zebra quantum", topK: 5));
    }

    [Fact]
    public async Task Search_BeforeIndexing_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _retriever.SearchAsync("anything", topK: 5));
    }
}
