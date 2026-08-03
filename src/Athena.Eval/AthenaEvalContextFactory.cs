using Athena.Core.Options;
using Athena.Core.Records;
using Athena.Eval.Metrics;
using Athena.Ingestion;
using Athena.Ingestion.Chunking;
using Athena.Ingestion.DocVectors;
using Athena.Ingestion.Embeddings;
using Athena.Ingestion.Enrichment;
using Athena.Ingestion.Extraction;
using Athena.Retrieval;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace Athena.Eval;

/// <summary>
/// Builds the real system for one configuration: runs the ingestion pipeline in-process with
/// the configured chunker/doc-vector strategy, then stands up the Part B retrieval stack over
/// the result. Config keys: chunker (fixed|section), docvec (centroid|composite), retriever
/// (dense|bm25|hybrid|hybrid-rerank).
///
/// Re-ingesting per configuration is the point, not waste: a chunker ablation only means
/// something if each arm retrieves over its own chunking. With warm DI/summary/embedding
/// caches an arm re-ingests without network calls except embeddings for chunk texts that
/// chunker produces for the first time (hard constraint 11 keeps DI at one call per document).
/// </summary>
public sealed class AthenaEvalContextFactory : IEvalContextFactory
{
    /// <summary>QA retrieval depth — the brief's @6 in Context Recall@6 / Precision@6.</summary>
    public const int RetrievedK = 6;

    private readonly AthenaOptions _options;
    private readonly string _corpusDirectory;
    private readonly ILoggerFactory _loggerFactory;

    public AthenaEvalContextFactory(AthenaOptions options, string corpusDirectory, ILoggerFactory loggerFactory)
    {
        _options = options;
        _corpusDirectory = corpusDirectory;
        _loggerFactory = loggerFactory;
    }

    public async Task<EvalContext> CreateAsync(EvalConfig config, CancellationToken ct = default)
    {
        IngestedStack stack = await BuildStackAsync(config, ct);

        // docId-aware core so OCR Delta can judge each copy on its own chunks; the plain
        // retrieve used by every other metric is the same core with no filter.
        Func<string, string?, CancellationToken, Task<IReadOnlyList<Passage>>> retrieveCore =
            config.Get("retriever", "hybrid-rerank") switch
            {
                "dense" => (q, d, c) => stack.Dense.SearchAsync(q, RetrievedK, d, c),
                "bm25" => (q, d, c) => stack.Lexical.SearchAsync(q, RetrievedK, d, c),
                // "hybrid" is the RRF-only arm of ablation 1: same pipeline, reranking disabled.
                "hybrid" => Hybrid(stack.Dense, stack.Lexical, new PassthroughReranker()),
                "hybrid-rerank" => Hybrid(stack.Dense, stack.Lexical, new SkPromptReranker(stack.Kernel)),
                var name => throw new ArgumentException(
                    $"Unknown retriever '{name}' (use dense|bm25|hybrid|hybrid-rerank)."),
            };
        Func<string, CancellationToken, Task<IReadOnlyList<Passage>>> retrieve =
            (q, c) => retrieveCore(q, null, c);

        var retrievalSource = new QaRetrievalSource(retrieve);
        var answerSource = new QaAnswerSource(retrieve, stack.Kernel,
            new Athena.Filters.JsonlCitationViolationLog(Path.Combine(
                Path.GetDirectoryName(_corpusDirectory)!, "logs", "citation-violations.jsonl")));

        var qaMetrics = new List<IMetric<QaCase>>
        {
            new ContextRecallAtK(retrievalSource, RetrievedK),
            new ContextPrecisionAtK(retrievalSource, RetrievedK),
            new Faithfulness(answerSource, stack.Kernel),
            new AnswerCorrectness(answerSource, stack.Kernel),
            new AbstentionAccuracy(answerSource),
            new CitationViolationRate(answerSource),
        };

        IReadOnlyList<DocRecord> docs = await LoadDocsAsync(stack.Docs, ct);

        // OCR Delta pairs are DISCOVERED from the ingested corpus via the "{id}-SCAN"
        // ingestion convention — any document rasterised in future gets its delta for free.
        foreach (DocRecord scan in docs.Where(d => d.DocId.EndsWith("-SCAN", StringComparison.Ordinal)))
        {
            string nativeId = scan.DocId[..^"-SCAN".Length];
            if (docs.Any(d => string.Equals(d.DocId, nativeId, StringComparison.Ordinal)))
            {
                qaMetrics.Add(new OcrDelta(retrieveCore, nativeId, scan.DocId, scan.PageCount));
            }
        }

        // Rec pipeline knobs are config axes so ablations 3 and 4 are pure config sweeps.
        double lambda = double.Parse(config.Get("lambda", "0.7"),
            System.Globalization.CultureInfo.InvariantCulture);
        bool dedupOn = !string.Equals(config.Get("dedup", "on"), "off", StringComparison.Ordinal);
        var recommendationSource = new MoreLikeThisRecommendationSource(
            // The scan is an OCR diagnostic artifact, not a recommendable reading — and its
            // lineage group would otherwise let it shadow its native original.
            docs.Where(d => !d.DocId.EndsWith("-SCAN", StringComparison.Ordinal)).ToList(),
            new Athena.Recommendation.RecommendationScorer(),
            dedupOn ? new Athena.Recommendation.LineageDuplicateResolver() : null,
            new Athena.Recommendation.MmrDiversifier(),
            lambda,
            topK: 5,
            asOf: DateTimeOffset.UtcNow);

        return new EvalContext(
            qaMetrics,
            recMetrics:
            [
                new NdcgAtK(recommendationSource, k: 5),
                new MeanReciprocalRank(recommendationSource),
                new CatalogueCoverage(recommendationSource),
                new IntraListDiversity(recommendationSource),
                new DuplicateLeakage(recommendationSource),
            ],
            resources: [stack]);
    }

    private static async Task<IReadOnlyList<DocRecord>> LoadDocsAsync(
        VectorStoreCollection<string, DocRecord> collection, CancellationToken ct)
    {
        var docs = new List<DocRecord>();
        await foreach (DocRecord doc in collection.GetAsync(_ => true, top: int.MaxValue, cancellationToken: ct))
        {
            docs.Add(doc);
        }

        return docs;
    }

    /// <summary>
    /// Ingests the corpus under one configuration and stands up the Part B retrieval stack —
    /// shared by the metric context above and by non-metric commands (e.g. the §9.1 lambda
    /// sweep) that need the same ingested system without the metric wrapping.
    /// </summary>
    public async Task<IngestedStack> BuildStackAsync(EvalConfig config, CancellationToken ct = default)
    {
        // --- extraction: same one-cached-DI-call-per-document stack as IndexCommand ---
        var diClient = new DocumentIntelligenceClient(
            new Uri(_options.AzureDocumentIntelligence.Endpoint),
            new AzureKeyCredential(_options.AzureDocumentIntelligence.ApiKey));
        var analyzer = new AzureDocumentAnalyzer(diClient, Path.Combine(_corpusDirectory, ".di-cache"));

        IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddOpenAIChatCompletion(_options.OpenAI.ChatModelId, _options.OpenAI.ApiKey);
#pragma warning disable SKEXP0010 // embedding-generator registration still experimental in SK 1.78
        kernelBuilder.AddOpenAIEmbeddingGenerator(_options.OpenAI.EmbeddingModel, _options.OpenAI.ApiKey);
#pragma warning restore SKEXP0010
        Kernel kernel = kernelBuilder.Build();

        var embeddingGenerator = new CachingEmbeddingGenerator(
            kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            Path.Combine(_corpusDirectory, ".embedding-cache"),
            _options.OpenAI.EmbeddingModel);

        var tokenCounter = new Cl100kTokenCounter();
        IChunker chunker = config.Get("chunker", "fixed") switch
        {
            "fixed" => new FixedWindowChunker(tokenCounter),
            "section" => new SectionAwareChunker(tokenCounter),
            var name => throw new ArgumentException($"Unknown chunker '{name}' (use fixed|section)."),
        };
        IDocumentVectorStrategy docVectorStrategy = config.Get("docvec", "centroid") switch
        {
            "centroid" => new CentroidStrategy(),
            "composite" => new CompositeStrategy(embeddingGenerator),
            var name => throw new ArgumentException($"Unknown doc-vector strategy '{name}' (use centroid|composite)."),
        };

        var vectorStore = new InMemoryVectorStore();
        VectorStoreCollection<string, ChunkRecord> chunkCollection =
            vectorStore.GetCollection<string, ChunkRecord>("chunks");
        VectorStoreCollection<string, DocRecord> docCollection =
            vectorStore.GetCollection<string, DocRecord>("docs");

        var pipeline = new IngestionPipeline(
            new AzureLayoutTextExtractor(analyzer),
            new AzureLayoutParagraphExtractor(analyzer),
            new AzureTableExtractor(analyzer, _loggerFactory.CreateLogger<AzureTableExtractor>()),
            new DocnetTextLayerProbe(),
            chunker,
            new CachedDocumentSummariser(
                new SkDocumentSummariser(kernel, tokenCounter),
                Path.Combine(_corpusDirectory, ".enrichment-cache")),
            docVectorStrategy,
            embeddingGenerator,
            chunkCollection,
            docCollection,
            _loggerFactory.CreateLogger<IngestionPipeline>());
        await pipeline.RunAsync(_corpusDirectory, ct);

        // --- Part B retrieval stack over the freshly ingested chunks ---
        var chunks = new List<ChunkRecord>();
        await foreach (ChunkRecord chunk in chunkCollection.GetAsync(_ => true, top: int.MaxValue,
                           cancellationToken: ct))
        {
            chunks.Add(chunk);
        }

        var lexical = new LuceneLexicalRetriever();
        lexical.Index(chunks);
        var dense = new DenseRetriever(chunkCollection, embeddingGenerator);

        return new IngestedStack(kernel, embeddingGenerator, chunkCollection, docCollection,
            dense, lexical);
    }

    private static Func<string, string?, CancellationToken, Task<IReadOnlyList<Passage>>> Hybrid(
        IDenseRetriever dense, ILexicalRetriever lexical, IReranker reranker)
    {
        var retriever = new HybridRetriever(dense, lexical, new ReciprocalRankFusion(), reranker);
        return (q, docId, c) => retriever.RetrieveAsync(q, RetrievedK, docId, c);
    }
}
