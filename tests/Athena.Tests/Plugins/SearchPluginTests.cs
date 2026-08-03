using Athena.Plugins;
using Athena.Retrieval;

namespace Athena.Tests.Plugins;

public sealed class SearchPluginTests
{
    private sealed class FakeRetriever(IReadOnlyList<Passage> results) : IDenseRetriever, ILexicalRetriever
    {
        public Task<IReadOnlyList<Passage>> SearchAsync(
            string query, int topK, string? docId = null, CancellationToken ct = default)
            => Task.FromResult(results);
    }

    private sealed class KeepAllReranker : IReranker
    {
        public Task<IReadOnlyList<Passage>> RerankAsync(
            string query, IReadOnlyList<Passage> candidates, int topK, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Passage>>(candidates.Take(topK).ToList());
    }

    private static HybridRetriever Retriever(params Passage[] passages)
    {
        var fake = new FakeRetriever(passages);
        return new HybridRetriever(fake, fake, new ReciprocalRankFusion(), new KeepAllReranker());
    }

    private static readonly Passage A1P11 = new(
        "c1", "A1", "Principles for Operational Resilience", 11, "Banks should develop plans.", 0.9);

    [Fact]
    public void FormatPassages_HeadsEachPassageWithTheCitationTag()
    {
        string formatted = SearchPlugin.FormatPassages([A1P11]);

        // The header line IS the citation contract with answer.yaml — exact form [Title, p.N].
        Assert.StartsWith("[Principles for Operational Resilience, p.11] (doc: A1)", formatted);
        Assert.Contains("Banks should develop plans.", formatted);
    }

    [Fact]
    public async Task HybridSearch_RecordsRetrievedContext_ForTheGroundingGuard()
    {
        var accessor = new RetrievedContextAccessor();
        var plugin = new SearchPlugin(Retriever(A1P11), accessor);

        await plugin.HybridSearchAsync("resilience plans", topK: 6);

        Passage recorded = Assert.Single(accessor.Current);
        Assert.Equal("c1", recorded.ChunkId);
    }

    [Fact]
    public async Task HybridSearch_NoHits_SaysSoInsteadOfReturningEmptyString()
    {
        var plugin = new SearchPlugin(Retriever());

        Assert.Equal("No passages found.", await plugin.HybridSearchAsync("anything"));
    }

    [Fact]
    public async Task AnswerQuestion_NothingRetrieved_AbstainsWithoutAModelCall()
    {
        var plugin = new SearchPlugin(Retriever());

        // Kernel null is safe here precisely because the no-context path must never reach the
        // prompt function — this test pins that.
        string answer = await plugin.AnswerQuestionAsync(kernel: null!, "Out of corpus question?");

        Assert.Equal("INSUFFICIENT_CONTEXT", answer);
    }
}
