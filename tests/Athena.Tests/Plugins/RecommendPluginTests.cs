using Athena.Core.Records;
using Athena.Plugins;
using Athena.Recommendation;
using Athena.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace Athena.Tests.Plugins;

/// <summary>
/// End-to-end plugin flows over an InMemory doc collection with the REAL pure components —
/// only embeddings and chunk retrieval are faked, so no test needs a model or network.
/// </summary>
public sealed class RecommendPluginTests : IAsyncLifetime
{
    private static readonly ReadOnlyMemory<float> QueryVector = new float[] { 1f, 0f };

    private VectorStoreCollection<string, DocRecord> _docs = null!;
    private SessionInterestProfileStore _profiles = null!;
    private RecommendPlugin _plugin = null!;

    private sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(QueryVector)).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeChunkRetriever(IReadOnlyList<Passage> hits) : IDenseRetriever, ILexicalRetriever
    {
        public Task<IReadOnlyList<Passage>> SearchAsync(
            string query, int topK, string? docId = null, CancellationToken ct = default)
            => Task.FromResult(hits);
    }

    private sealed class KeepAllReranker : IReranker
    {
        public Task<IReadOnlyList<Passage>> RerankAsync(
            string query, IReadOnlyList<Passage> candidates, int topK, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Passage>>(candidates.Take(topK).ToList());
    }

    private static DocRecord Doc(string id, double degrees, string? group = null,
                                 string published = "2024-01-01")
    {
        double radians = degrees * Math.PI / 180.0;
        return new DocRecord
        {
            DocId = id,
            Title = $"Title of {id}",
            Topics = ["rag", $"topic-{id}"],
            ChunkCount = 10,
            PublishedOn = DateTimeOffset.Parse(published + "T00:00:00Z"),
            LineageGroup = group,
            Embedding = new[] { (float)Math.Cos(radians), (float)Math.Sin(radians) },
        };
    }

    public async Task InitializeAsync()
    {
        _docs = new InMemoryVectorStore().GetCollection<string, DocRecord>("docs");
        await _docs.EnsureCollectionExistsAsync();
        // Angles stay below the 0.95 dedup ceiling relative to the seed and to each other
        // (cos30° ≈ 0.866): similar enough to rank, distinct enough not to be collapsed as
        // near-copies — the ceiling case has its own dedicated resolver tests.
        await _docs.UpsertAsync(new[]
        {
            Doc("B3", degrees: 0),                                            // the seed
            Doc("B4", degrees: 30),
            Doc("C1", degrees: 60),
            Doc("A1", degrees: 85, group: "A1", published: "2021-03-31"),     // lineage final
            Doc("A2", degrees: 86, group: "A1", published: "2020-08-06"),     // lineage draft
        });

        _profiles = new SessionInterestProfileStore();
        var chunkFake = new FakeChunkRetriever([new Passage("c1", "B4", "Title of B4", 3, "t", 1)]);
        _plugin = new RecommendPlugin(
            new RecommendationScorer(),
            new MmrDiversifier(),
            new LineageDuplicateResolver(),
            _profiles,
            _docs,
            new HybridRetriever(chunkFake, chunkFake, new ReciprocalRankFusion(), new KeepAllReranker()),
            new FakeEmbeddings(),
            TimeProvider.System,
            sessionId: "test-session");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MoreLikeThis_ExcludesSeedAndSuppressedDraft_AndNamesTheSupersession()
    {
        string result = await _plugin.MoreLikeThisAsync("B3", topK: 5);

        Assert.DoesNotContain("[B3]", result);           // never recommends the seed
        Assert.DoesNotContain("[A2]", result);           // draft collapsed into A1
        Assert.Contains("[A1]", result);
        Assert.Contains("Supersedes A2, Aug 2020.", result); // provenance reached the reason
        Assert.Contains("signals: docSim=", result);     // verifiable breakdown next to each reason
    }

    [Fact]
    public async Task MoreLikeThis_AcceptsAnExactTitleInsteadOfAnId()
    {
        string result = await _plugin.MoreLikeThisAsync("Title of B3", topK: 3);

        Assert.Contains("[B4]", result);
        Assert.DoesNotContain("[B3]", result);
    }

    [Fact]
    public async Task MoreLikeThis_UnknownDoc_SaysSoInsteadOfGuessing()
    {
        string result = await _plugin.MoreLikeThisAsync("Z9", topK: 5);

        Assert.Equal("No document with id or title 'Z9' exists in the library.", result);
    }

    [Fact]
    public async Task MoreLikeThis_UniqueTitleFragment_ResolvesTheSeed()
    {
        // "the RAPTOR one" case: models pass a paper's colloquial name, not our exact title.
        string result = await _plugin.MoreLikeThisAsync("of B3", topK: 3);

        Assert.DoesNotContain("[B3]", result); // resolved as seed, so excluded from results
        Assert.Contains("[B4]", result);
    }

    [Fact]
    public async Task MoreLikeThis_AmbiguousFragment_ListsCandidatesInsteadOfGuessing()
    {
        string result = await _plugin.MoreLikeThisAsync("Title of", topK: 3);

        Assert.StartsWith("Multiple documents match", result);
        Assert.Contains("B3", result);
    }

    [Fact]
    public async Task MoreLikeThis_Lambda1_RanksBySimilarityToSeed()
    {
        string result = await _plugin.MoreLikeThisAsync("B3", topK: 2, lambda: 1.0);

        // B4 (15°) is nearest the seed; C1 (40°) next. Pure relevance keeps that order.
        int b4 = result.IndexOf("[B4]", StringComparison.Ordinal);
        int c1 = result.IndexOf("[C1]", StringComparison.Ordinal);
        Assert.True(b4 >= 0 && c1 > b4, result);
    }

    [Fact]
    public async Task RecommendForUser_NoHistory_AsksForATurnFirst()
    {
        string result = await _plugin.RecommendForUserAsync();

        Assert.Equal("No session history yet — ask a question or name a topic first.", result);
    }

    [Fact]
    public async Task RecommendForUser_ExcludesAlreadySurfacedDocs()
    {
        await _profiles.UpdateAsync("test-session", QueryVector);
        await _profiles.UpdateAsync("test-session", QueryVector); // two turns → plural phrasing
        await _profiles.MarkSurfacedAsync("test-session", ["B3", "B4"]);

        string result = await _plugin.RecommendForUserAsync(topK: 5);

        Assert.DoesNotContain("[B3]", result);
        Assert.DoesNotContain("[B4]", result);
        Assert.Contains("[C1]", result);
        Assert.Contains("questions circled around", result); // profile-driven phrasing
    }

    [Fact]
    public async Task RecommendForQuery_MarksSurfaced_ButLeavesProfileToTheChatBoundary()
    {
        string result = await _plugin.RecommendForQueryAsync("retrieval augmented generation", topK: 3);

        // Profile updates happen once per USER turn at the chat boundary — a turn that calls
        // two functions (§10 utterance 5) must not double-count. The plugin only records
        // what it surfaced.
        Assert.Null(await _profiles.GetAsync("test-session"));
        Assert.NotEmpty(await _profiles.GetAlreadySurfacedAsync("test-session"));
        Assert.Contains("signals:", result);
    }
}
