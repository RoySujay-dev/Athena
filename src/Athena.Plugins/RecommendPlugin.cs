using System.ComponentModel;
using System.Text;
using Athena.Core.Records;
using Athena.Recommendation;
using Athena.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
// The Athena.Recommendation NAMESPACE shadows the Recommendation TYPE from inside
// Athena.Plugins (enclosing-namespace lookup wins over usings) — alias around it.
using Rec = Athena.Recommendation.Recommendation;

namespace Athena.Plugins;

/// <summary>
/// The recommendation plugin surface (brief §9.6). Thin adapter: every ranking decision lives
/// in the injected pure components (scorer, MMR, dedup, affinity) — this class only shapes
/// arguments, sequences the calls, and serialises results. The [Description] strings are the
/// brief's verbatim; they are the entire routing signal (hard constraint 1).
///
/// DEVIATION from the brief's §9.6 ctor (README-documented): recommend_for_query cannot exist
/// without a query embedder and a chunk retriever, recency needs a clock, and the scoped
/// profile store needs this session's id — all injected, none inventable from the brief's
/// five parameters alone.
/// </summary>
public sealed class RecommendPlugin
{
    /// <summary>Chunk hits fed to the scorer's aggregation — the QA pipeline's rerank shortlist depth.</summary>
    private const int ChunkHitDepth = 10;

    private readonly IRecommendationScorer _scorer;
    private readonly IDiversifier _diversifier;
    private readonly INearDuplicateResolver _dedup;
    private readonly IInterestProfileStore _profiles;
    private readonly VectorStoreCollection<string, DocRecord> _docs;
    private readonly HybridRetriever _retriever;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly TimeProvider _clock;
    private readonly string _sessionId;

    public RecommendPlugin(IRecommendationScorer scorer, IDiversifier diversifier,
                           INearDuplicateResolver dedup, IInterestProfileStore profiles,
                           VectorStoreCollection<string, DocRecord> docs,
                           HybridRetriever retriever,
                           IEmbeddingGenerator<string, Embedding<float>> embeddings,
                           TimeProvider clock, string sessionId)
    {
        _scorer = scorer;
        _diversifier = diversifier;
        _dedup = dedup;
        _profiles = profiles;
        _docs = docs;
        _retriever = retriever;
        _embeddings = embeddings;
        _clock = clock;
        _sessionId = sessionId;
    }

    [KernelFunction("more_like_this")]
    [Description("Given one document the user has named, recommends other documents in the " +
                 "library that are similar to it. Use when the user points at a specific " +
                 "document and asks for related reading.")]
    public async Task<string> MoreLikeThisAsync(
        [Description("Id or exact title of the seed document")] string docId,
        [Description("How many documents to recommend")] int topK = 5,
        [Description("Relevance/diversity trade-off; 1.0 is pure relevance")] double lambda = 0.7,
        CancellationToken ct = default)
    {
        IReadOnlyList<DocRecord> all = await LoadDocsAsync(ct);
        DocRecord? seed = all.FirstOrDefault(d =>
            string.Equals(d.DocId, docId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.Title, docId, StringComparison.OrdinalIgnoreCase));
        if (seed is null)
        {
            // Users name papers by fragment ("the RAPTOR one"), not by our exact title. A
            // UNIQUE substring match resolves the entity; an ambiguous one asks. This is
            // argument→catalog lookup inside a function the model already chose — not intent
            // routing, which stays entirely with FunctionChoiceBehavior.Auto().
            var matches = all
                .Where(d => d.Title.Contains(docId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
            {
                return $"Multiple documents match '{docId}': " +
                    string.Join("; ", matches.Select(m => $"{m.DocId} ({m.Title})")) +
                    ". Ask again with one of these ids.";
            }

            seed = matches.SingleOrDefault();
        }

        if (seed is null)
        {
            return $"No document with id or title '{docId}' exists in the library.";
        }

        var candidates = all.Where(d => !ReferenceEquals(d, seed)).ToList();
        // Doc-similarity flow (§9.1): the seed's vector IS the query; no chunk hits here.
        IReadOnlyList<Rec> scored = _scorer.Score(
            seed.Title, seed.Embedding, candidates, chunkHits: [], _clock.GetUtcNow());

        IReadOnlyList<Rec> results = SelectAndExplain(
            scored, ById(candidates), seed.Embedding, seed, topK, lambda,
            docHits: [], recentQueryCount: 0, contextTopics: seed.Topics);
        await _profiles.MarkSurfacedAsync(_sessionId, results.Select(r => r.DocId), ct);
        return Format(results);
    }

    [KernelFunction("recommend_for_query")]
    // Part E hardening beyond the brief's verbatim text: the positive boundary explicitly
    // covers "what should I read about X" WITH a named topic — a named topic makes the
    // utterance look like a factual question, and this sentence claims it back.
    [Description("Recommends documents worth reading on a stated topic. Use when the user asks " +
                 "what to read about something — including 'what should I read about X' where " +
                 "X is a named topic; a reading request with a topic in it is still a reading " +
                 "request, not a factual question about that topic.")]
    public async Task<string> RecommendForQueryAsync(
        [Description("The topic the user wants reading on")] string query,
        [Description("How many documents to recommend")] int topK = 5,
        CancellationToken ct = default)
    {
        IReadOnlyList<DocRecord> all = await LoadDocsAsync(ct);
        GeneratedEmbeddings<Embedding<float>> embedded = await _embeddings.GenerateAsync(
            [query], cancellationToken: ct);
        ReadOnlyMemory<float> queryVector = embedded[0].Vector;
        IReadOnlyList<Passage> chunkHits = await _retriever.RetrieveAsync(query, ChunkHitDepth, null, ct);

        IReadOnlyList<Rec> scored = _scorer.Score(
            query, queryVector, all, chunkHits, _clock.GetUtcNow());

        IReadOnlyList<Rec> results = SelectAndExplain(
            scored, ById(all), queryVector, seed: null, topK, lambda: 0.7,
            docHits: chunkHits, recentQueryCount: 0, contextTopics: []);

        // NOTE: the §9.4 profile update (decay*profile + (1-decay)*embed(query)) deliberately
        // does NOT happen here. One user turn can invoke two functions (§10 utterance 5 calls
        // answer_question AND a recommender), so per-function updates would double-count a
        // single query. The chat boundary (agent driver / web chat loop) updates the profile
        // exactly once per user turn.
        await _profiles.MarkSurfacedAsync(_sessionId, results.Select(r => r.DocId), ct);
        return Format(results);
    }

    [KernelFunction("recommend_for_user")]
    [Description("Recommends documents based on what this user has asked about so far in the " +
                 "session. Use for open-ended follow-ups such as 'what else should I read?' " +
                 "where no topic or document is named.")]
    public async Task<string> RecommendForUserAsync(
        [Description("How many documents to recommend")] int topK = 5,
        CancellationToken ct = default)
    {
        InterestSnapshot snapshot = await _profiles.GetSnapshotAsync(_sessionId, ct);
        if (snapshot.Profile is null && snapshot.RecentQueries.Count == 0)
        {
            return "No session history yet — ask a question or name a topic first.";
        }

        IReadOnlyList<DocRecord> all = await LoadDocsAsync(ct);
        var candidates = all.Where(d => !snapshot.SurfacedDocIds.Contains(d.DocId)).ToList();

        // Drift-safe ranking: ProfileAffinity scores by MAX similarity over the recent query
        // vectors (§9.4 crossover), so the affinity lands in the DocSim slot of the breakdown
        // and the profile flow stays honest about which signal it used.
        var scored = candidates
            .Select(d => new Rec(
                d.DocId, d.Title, Reason: string.Empty, d.Topics,
                Score: ProfileAffinity.Score(d.Embedding, snapshot),
                new SignalBreakdown(
                    DocSim: ProfileAffinity.Score(d.Embedding, snapshot),
                    ChunkAggregate: 0, Recency: 0, ChunkHits: 0, BestHitRank: 0)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.DocId, StringComparer.Ordinal)
            .ToList();

        ReadOnlyMemory<float> mmrSeed = snapshot.Profile ?? snapshot.RecentQueries[0];
        IReadOnlyList<Rec> results = SelectAndExplain(
            scored, ById(candidates), mmrSeed, seed: null, topK, lambda: 0.7,
            docHits: [], recentQueryCount: snapshot.RecentQueries.Count, contextTopics: []);
        await _profiles.MarkSurfacedAsync(_sessionId, results.Select(r => r.DocId), ct);
        return Format(results);
    }

    /// <summary>
    /// The shared tail of every flow: dedup on the scored ranking → MMR over the survivors →
    /// reasons composed from the signals and provenance. Pure components do all the work.
    /// </summary>
    private IReadOnlyList<Rec> SelectAndExplain(
        IReadOnlyList<Rec> scored,
        IReadOnlyDictionary<string, DocRecord> docsById,
        ReadOnlyMemory<float> mmrSeed,
        DocRecord? seed,
        int topK,
        double lambda,
        IReadOnlyList<Passage> docHits,
        int recentQueryCount,
        IReadOnlyList<string> contextTopics)
    {
        var rankedDocs = scored.Select(r => docsById[r.DocId]).ToList();
        IReadOnlyList<ResolvedDoc> resolved = _dedup.ResolveWithProvenance(rankedDocs, seed);
        IReadOnlyList<DocRecord> selected = _diversifier.Select(
            mmrSeed, resolved.Select(r => r.Doc).ToList(), topK, lambda);

        var scoredById = scored.ToDictionary(r => r.DocId, StringComparer.Ordinal);
        var provenanceById = resolved.ToDictionary(r => r.Doc.DocId, StringComparer.Ordinal);
        return selected
            .Select(doc => ReasonGenerator.WithReason(new ReasonInputs(
                scoredById[doc.DocId],
                doc.PublishedOn,
                docHits.Where(h => string.Equals(h.DocId, doc.DocId, StringComparison.Ordinal)).ToList(),
                provenanceById[doc.DocId].SuppressedSiblings,
                contextTopics,
                recentQueryCount)))
            .ToList();
    }

    private async Task<IReadOnlyList<DocRecord>> LoadDocsAsync(CancellationToken ct)
    {
        var docs = new List<DocRecord>();
        await foreach (DocRecord doc in _docs.GetAsync(_ => true, top: int.MaxValue, cancellationToken: ct))
        {
            docs.Add(doc);
        }

        return docs;
    }

    private static IReadOnlyDictionary<string, DocRecord> ById(IReadOnlyList<DocRecord> docs)
        => docs.ToDictionary(d => d.DocId, StringComparer.Ordinal);

    /// <summary>
    /// §9.5 presentation: title, topics, score, signal-grounded reason — plus the breakdown
    /// line next to each reason so every claim is verifiable against its numbers.
    /// </summary>
    private static string Format(IReadOnlyList<Rec> recommendations)
    {
        if (recommendations.Count == 0)
        {
            return "No recommendations available.";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < recommendations.Count; i++)
        {
            Rec rec = recommendations[i];
            sb.AppendLine($"{i + 1}. {rec.Title} [{rec.DocId}] — score {rec.Score:0.00}");
            sb.AppendLine($"   {rec.Reason}");
            sb.AppendLine($"   topics: {string.Join(", ", rec.Topics)}");
            sb.AppendLine($"   signals: {ReasonGenerator.FormatBreakdown(rec.Signals)}");
        }

        return sb.ToString().TrimEnd();
    }
}
