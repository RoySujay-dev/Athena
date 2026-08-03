using System.Text;
using Athena.Core.Options;
using Athena.Core.Records;
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
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace Athena.Web.Services;

/// <summary>One manifest row on the Corpus page: what exists on disk and in the stores.</summary>
public sealed record CorpusDocStatus(
    string DocId, string Title, string Cluster, bool PdfPresent, long PdfBytes,
    bool HasScannedVariant, bool Ingested, int ChunkCount);

/// <summary>
/// Owns the ONE retrieval stack for the app — but, unlike the eval harness, it starts EMPTY:
/// nothing is ingested until the user picks documents on the Corpus page and presses Ingest.
/// Startup-time auto-ingestion of the whole manifest made every launch pay the full pipeline
/// before the first question; selective ingestion pays only for what the demo needs. The
/// collections and Lucene index are shared by every circuit; ingestion is serialized behind a
/// semaphore and additive (hard constraint 11's caches make re-ingesting a document cheap).
/// </summary>
public sealed class CorpusService : IDisposable
{
    private readonly SemaphoreSlim _ingestLock = new(1, 1);
    private readonly IngestionPipeline? _pipeline;
    private readonly string? _corpusDirectory;

    public AthenaOptions Options { get; }

    /// <summary>Repo root (parent of corpus/) — logs/ paths hang off it.</summary>
    public string RepoRoot { get; } = string.Empty;

    public string? FailureReason { get; }

    public Kernel Kernel { get; } = null!;

    public CachingEmbeddingGenerator EmbeddingGenerator { get; } = null!;

    public VectorStoreCollection<string, ChunkRecord> Chunks { get; } = null!;

    public VectorStoreCollection<string, DocRecord> Docs { get; } = null!;

    public DenseRetriever Dense { get; } = null!;

    public LuceneLexicalRetriever Lexical { get; } = null!;

    public bool IsBusy { get; private set; }

    public IngestionReport? LastReport { get; private set; }

    public int IngestedDocCount { get; private set; }

    /// <summary>Raised after fetch/ingest completes so open pages can refresh their status.</summary>
    public event Action? Changed;

    public CorpusService(AthenaOptions options, ILoggerFactory loggerFactory)
    {
        Options = options;

        _corpusDirectory = CorpusLocator.LocateCorpusDirectory();
        if (_corpusDirectory is null)
        {
            FailureReason = "Could not locate corpus/manifest.json above the working directory. " +
                            "Run from within the repository.";
            return;
        }

        RepoRoot = Path.GetDirectoryName(_corpusDirectory)!;
        if (string.IsNullOrEmpty(options.OpenAI.ApiKey)
            || string.IsNullOrEmpty(options.AzureDocumentIntelligence.Endpoint))
        {
            FailureReason = "Missing secrets. Set OpenAI:{ApiKey,ChatModelId,EmbeddingModel} and " +
                            "AzureDocumentIntelligence:{Endpoint,ApiKey} via 'dotnet user-secrets' " +
                            "(shared store — one set serves every Athena host).";
            return;
        }

        // Same wiring as the eval factory's stack (fixed chunker, centroid doc vectors), minus
        // the ingest-everything step — RunAsync happens per user selection instead.
        IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddOpenAIChatCompletion(options.OpenAI.ChatModelId, options.OpenAI.ApiKey);
#pragma warning disable SKEXP0010 // embedding-generator registration still experimental in SK 1.78
        kernelBuilder.AddOpenAIEmbeddingGenerator(options.OpenAI.EmbeddingModel, options.OpenAI.ApiKey);
#pragma warning restore SKEXP0010
        Kernel = kernelBuilder.Build();

        EmbeddingGenerator = new CachingEmbeddingGenerator(
            Kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            Path.Combine(_corpusDirectory, ".embedding-cache"),
            options.OpenAI.EmbeddingModel);

        var vectorStore = new InMemoryVectorStore();
        Chunks = vectorStore.GetCollection<string, ChunkRecord>("chunks");
        Docs = vectorStore.GetCollection<string, DocRecord>("docs");
        Dense = new DenseRetriever(Chunks, EmbeddingGenerator);
        Lexical = new LuceneLexicalRetriever();
        // An empty index (not a missing one) so a question asked before any ingestion returns
        // "no passages" instead of throwing from the lexical arm.
        Lexical.Index([]);

        var diClient = new DocumentIntelligenceClient(
            new Uri(options.AzureDocumentIntelligence.Endpoint),
            new AzureKeyCredential(options.AzureDocumentIntelligence.ApiKey));
        var analyzer = new AzureDocumentAnalyzer(diClient, Path.Combine(_corpusDirectory, ".di-cache"));
        var tokenCounter = new Cl100kTokenCounter();
        _pipeline = new IngestionPipeline(
            new AzureLayoutTextExtractor(analyzer),
            new AzureLayoutParagraphExtractor(analyzer),
            new AzureTableExtractor(analyzer, loggerFactory.CreateLogger<AzureTableExtractor>()),
            new DocnetTextLayerProbe(),
            // Section-aware is the measured winner on Context Recall@6 (0.900 vs 0.750 on the
            // 25-case gold set) — see README design decision 1. The eval harness still holds
            // chunker=fixed constant for the other ablations, so the two differ deliberately.
            new SectionAwareChunker(tokenCounter),
            new CachedDocumentSummariser(
                new SkDocumentSummariser(Kernel, tokenCounter),
                Path.Combine(_corpusDirectory, ".enrichment-cache")),
            new CentroidStrategy(),
            EmbeddingGenerator,
            Chunks,
            Docs,
            loggerFactory.CreateLogger<IngestionPipeline>());
    }

    public async Task<IReadOnlyList<CorpusDocStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        if (_corpusDirectory is null)
        {
            return [];
        }

        // The in-memory collections only exist after the first ingestion creates them; the
        // status page must read cleanly before that.
        await Chunks.EnsureCollectionExistsAsync(ct);
        await Docs.EnsureCollectionExistsAsync(ct);

        var ingested = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (DocRecord doc in Docs.GetAsync(_ => true, top: int.MaxValue, cancellationToken: ct))
        {
            ingested[doc.DocId] = doc.ChunkCount;
        }

        IngestedDocCount = ingested.Count;

        CorpusManifest manifest = await CorpusManifest.LoadAsync(
            Path.Combine(_corpusDirectory, "manifest.json"), ct);
        var rows = new List<CorpusDocStatus>(manifest.Documents.Count);
        foreach (ManifestEntry entry in manifest.Documents)
        {
            string pdfPath = Path.Combine(_corpusDirectory, $"{entry.Id}.pdf");
            var pdf = new FileInfo(pdfPath);
            rows.Add(new CorpusDocStatus(
                entry.Id, entry.Title, entry.Cluster,
                PdfPresent: pdf.Exists, PdfBytes: pdf.Exists ? pdf.Length : 0,
                HasScannedVariant: File.Exists(Path.Combine(_corpusDirectory, $"{entry.Id}-scanned.pdf")),
                Ingested: ingested.ContainsKey(entry.Id),
                ChunkCount: ingested.GetValueOrDefault(entry.Id)));
        }

        return rows;
    }

    /// <summary>
    /// Downloads the selected manifest PDFs that are missing (mirrors the ingestion CLI's
    /// fetch verb, including its %PDF magic-number guard against saved error pages).
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchAsync(
        IReadOnlyCollection<string> docIds, CancellationToken ct = default)
    {
        if (_corpusDirectory is null)
        {
            return [FailureReason ?? "Corpus not available."];
        }

        CorpusManifest manifest = await CorpusManifest.LoadAsync(
            Path.Combine(_corpusDirectory, "manifest.json"), ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Athena-corpus-fetch/0.1");

        var messages = new List<string>();
        foreach (ManifestEntry entry in manifest.Documents.Where(d => docIds.Contains(d.Id)))
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(_corpusDirectory, $"{entry.Id}.pdf");
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                continue;
            }

            try
            {
                byte[] bytes = await http.GetByteArrayAsync(entry.Url, ct);
                if (bytes.Length < 4 || Encoding.ASCII.GetString(bytes, 0, 4) != "%PDF")
                {
                    messages.Add($"{entry.Id}: response is not a PDF ({bytes.Length:N0} bytes).");
                    continue;
                }

                await File.WriteAllBytesAsync(target, bytes, ct);
                messages.Add($"{entry.Id}: fetched {bytes.Length:N0} bytes.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                messages.Add($"{entry.Id}: {e.Message}");
            }
        }

        Changed?.Invoke();
        return messages;
    }

    /// <summary>
    /// Ingests the selected documents (their scanned variants travel with them) and rebuilds
    /// the lexical index over ALL chunks — dense and BM25 must never drift apart.
    /// </summary>
    public async Task<IngestionReport> IngestAsync(
        IReadOnlyCollection<string> docIds, CancellationToken ct = default)
    {
        if (_pipeline is null || _corpusDirectory is null)
        {
            throw new InvalidOperationException(FailureReason ?? "Corpus not available.");
        }

        await _ingestLock.WaitAsync(ct);
        IsBusy = true;
        try
        {
            IngestionReport report = await _pipeline.RunAsync(_corpusDirectory, ct, docIds);

            var allChunks = new List<ChunkRecord>();
            await foreach (ChunkRecord chunk in Chunks.GetAsync(_ => true, top: int.MaxValue,
                               cancellationToken: ct))
            {
                allChunks.Add(chunk);
            }

            Lexical.Index(allChunks);
            LastReport = report;
            return report;
        }
        finally
        {
            IsBusy = false;
            _ingestLock.Release();
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        Lexical?.Dispose();
        EmbeddingGenerator?.Dispose();
        _ingestLock.Dispose();
    }
}
