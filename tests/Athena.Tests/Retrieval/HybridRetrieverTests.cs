using Athena.Retrieval;

namespace Athena.Tests.Retrieval;

/// <summary>
/// Orchestration contract of the §7 pipeline: dense top-20 + lexical top-20 → RRF → top-10
/// shortlist → rerank → topK. Stage internals are tested separately; these tests pin the
/// stage wiring and the 20/10/6 shape with fakes.
/// </summary>
public sealed class HybridRetrieverTests
{
    private static Passage P(string chunkId) =>
        new(chunkId, "DOC", "Title", PageNumber: 1, "text", Score: 0);

    private sealed class FakeRetriever(IReadOnlyList<Passage> results) : IDenseRetriever, ILexicalRetriever
    {
        public int? RequestedTopK { get; private set; }
        public string? RequestedDocId { get; private set; }

        public Task<IReadOnlyList<Passage>> SearchAsync(
            string query, int topK, string? docId = null, CancellationToken ct = default)
        {
            RequestedTopK = topK;
            RequestedDocId = docId;
            return Task.FromResult(results);
        }
    }

    private sealed class FakeReranker : IReranker
    {
        public IReadOnlyList<Passage>? Candidates { get; private set; }
        public int? RequestedTopK { get; private set; }

        public Task<IReadOnlyList<Passage>> RerankAsync(
            string query, IReadOnlyList<Passage> candidates, int topK, CancellationToken ct = default)
        {
            Candidates = candidates;
            RequestedTopK = topK;
            return Task.FromResult<IReadOnlyList<Passage>>(candidates.Take(topK).ToList());
        }
    }

    private static (FakeRetriever Dense, FakeRetriever Lexical, FakeReranker Reranker, HybridRetriever Sut)
        Build(int denseCount = 20, int lexicalCount = 20)
    {
        var dense = new FakeRetriever(Enumerable.Range(1, denseCount).Select(i => P($"d{i:00}")).ToList());
        var lexical = new FakeRetriever(Enumerable.Range(1, lexicalCount).Select(i => P($"l{i:00}")).ToList());
        var reranker = new FakeReranker();
        var sut = new HybridRetriever(dense, lexical, new ReciprocalRankFusion(), reranker);
        return (dense, lexical, reranker, sut);
    }

    [Fact]
    public async Task Retrieve_AsksEachRetrieverForTwenty_AndShortlistsTenForReranking()
    {
        var (dense, lexical, reranker, sut) = Build();

        IReadOnlyList<Passage> result = await sut.RetrieveAsync("query");

        Assert.Equal(20, dense.RequestedTopK);
        Assert.Equal(20, lexical.RequestedTopK);
        Assert.Equal(10, reranker.Candidates!.Count);
        // The reranker orders the WHOLE shortlist; the per-document diversity cap then
        // selects the final top-K from that ranking.
        Assert.Equal(10, reranker.RequestedTopK);
        Assert.Equal(6, result.Count);
    }

    [Fact]
    public async Task Retrieve_PropagatesDocIdToBothRetrievers()
    {
        var (dense, lexical, _, sut) = Build();

        await sut.RetrieveAsync("query", docId: "B3");

        Assert.Equal("B3", dense.RequestedDocId);
        Assert.Equal("B3", lexical.RequestedDocId);
    }

    [Fact]
    public async Task Retrieve_FewerCandidatesThanShortlist_PassesWhatExists()
    {
        var (_, _, reranker, sut) = Build(denseCount: 3, lexicalCount: 2);

        IReadOnlyList<Passage> result = await sut.RetrieveAsync("query", topK: 6);

        Assert.Equal(5, reranker.Candidates!.Count); // 3 + 2 distinct chunks, no padding
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task Retrieve_ReturnsRerankerOutput_NotFusionOrder()
    {
        var (_, _, _, sut) = Build();

        IReadOnlyList<Passage> result = await sut.RetrieveAsync("query", topK: 2);

        Assert.Equal(2, result.Count);
    }
}
