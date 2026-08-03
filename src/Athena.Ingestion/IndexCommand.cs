using Athena.Core.Options;
using Athena.Core.Records;
using Athena.Ingestion.Chunking;
using Athena.Ingestion.DocVectors;
using Athena.Ingestion.Embeddings;
using Athena.Ingestion.Enrichment;
using Athena.Ingestion.Extraction;
using Athena.Ingestion.Lineage;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace Athena.Ingestion;

/// <summary>
/// Composition root for <c>-- index</c>: reads user-secrets (never appsettings — hard
/// constraint 3), wires the pipeline, runs it, and prints the report plus the sprint's DoD
/// diagnostics (chunk/doc counts, lineage groups, and the cos(A1,A2) / cos(A1,B3) checks).
/// </summary>
internal static class IndexCommand
{
    public static async Task<int> RunAsync(string chunkerName, string docVectorName, CancellationToken ct)
    {
        string? corpusDir = CorpusLocator.LocateCorpusDirectory();
        if (corpusDir is null)
        {
            Console.Error.WriteLine("Could not locate corpus/manifest.json above the current directory.");
            return 1;
        }

        AthenaOptions options = LoadOptions();
        if (string.IsNullOrEmpty(options.OpenAI.ApiKey)
            || string.IsNullOrEmpty(options.AzureDocumentIntelligence.Endpoint))
        {
            Console.Error.WriteLine(
                "Missing secrets. Set OpenAI:{ApiKey,ChatModelId,EmbeddingModel} and " +
                "AzureDocumentIntelligence:{Endpoint,ApiKey} via 'dotnet user-secrets'.");
            return 1;
        }

        using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information));

        // --- extraction: one cached DI analysis per document feeds every extractor ---
        var diClient = new DocumentIntelligenceClient(
            new Uri(options.AzureDocumentIntelligence.Endpoint),
            new AzureKeyCredential(options.AzureDocumentIntelligence.ApiKey));
        var analyzer = new AzureDocumentAnalyzer(diClient, Path.Combine(corpusDir, ".di-cache"));
        var textExtractor = new AzureLayoutTextExtractor(analyzer);
        var paragraphExtractor = new AzureLayoutParagraphExtractor(analyzer);
        var tableExtractor = new AzureTableExtractor(
            analyzer, loggerFactory.CreateLogger<AzureTableExtractor>());

        // --- LLM + embeddings (both disk-cached so warm re-index makes zero network calls) ---
        IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddOpenAIChatCompletion(options.OpenAI.ChatModelId, options.OpenAI.ApiKey);
        // SK's embedding-generator registration is still marked experimental (SKEXP0010) in
        // 1.78 even though it is the replacement for the obsoleted TextEmbeddingGeneration
        // service — recorded in README Deviations (API drift).
#pragma warning disable SKEXP0010
        kernelBuilder.AddOpenAIEmbeddingGenerator(options.OpenAI.EmbeddingModel, options.OpenAI.ApiKey);
#pragma warning restore SKEXP0010
        Kernel kernel = kernelBuilder.Build();

        using var embeddingGenerator = new CachingEmbeddingGenerator(
            kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            Path.Combine(corpusDir, ".embedding-cache"),
            options.OpenAI.EmbeddingModel);

        var tokenCounter = new Cl100kTokenCounter();
        var summariser = new CachedDocumentSummariser(
            new SkDocumentSummariser(kernel, tokenCounter),
            Path.Combine(corpusDir, ".enrichment-cache"));

        // --- strategy selection (CLI flags, so the Part F ablation can run both) ---
        IChunker chunker = chunkerName switch
        {
            "fixed" => new FixedWindowChunker(tokenCounter),
            "section" => new SectionAwareChunker(tokenCounter),
            _ => throw new ArgumentException($"Unknown chunker '{chunkerName}' (use fixed|section)."),
        };
        IDocumentVectorStrategy docVectorStrategy = docVectorName switch
        {
            "centroid" => new CentroidStrategy(),
            "composite" => new CompositeStrategy(embeddingGenerator),
            _ => throw new ArgumentException($"Unknown doc-vector strategy '{docVectorName}' (use centroid|composite)."),
        };

        var vectorStore = new InMemoryVectorStore();
        VectorStoreCollection<string, ChunkRecord> chunkCollection =
            vectorStore.GetCollection<string, ChunkRecord>("chunks");
        VectorStoreCollection<string, DocRecord> docCollection =
            vectorStore.GetCollection<string, DocRecord>("docs");

        var pipeline = new IngestionPipeline(
            textExtractor, paragraphExtractor, tableExtractor, new DocnetTextLayerProbe(),
            chunker, summariser, docVectorStrategy, embeddingGenerator, chunkCollection,
            docCollection, loggerFactory.CreateLogger<IngestionPipeline>());

        IngestionReport report = await pipeline.RunAsync(corpusDir, ct);

        PrintReport(report, chunker.Name, docVectorStrategy.Name);
        await PrintDiagnosticsAsync(docCollection, ct);
        return report.Warnings.Count == 0 ? 0 : 0; // warnings are informational, not failures
    }

    private static AthenaOptions LoadOptions()
    {
        // AddUserSecrets<T> resolves the UserSecretsId from T's ASSEMBLY — AthenaOptions lives
        // in Athena.Core, which deliberately has no UserSecretsId, so it must be this assembly.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(IndexCommand).Assembly)
            .Build();
        var options = new AthenaOptions();
        configuration.Bind(options);
        return options;
    }

    private static void PrintReport(IngestionReport report, string chunker, string docVector)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Ingestion report (chunker={chunker}, docvec={docVector}) ===");
        Console.WriteLine($"Documents processed : {report.DocumentsProcessed}");
        Console.WriteLine($"Chunks written      : {report.ChunksWritten}");
        Console.WriteLine($"Pages OCR-classified: {report.PagesOcrd}");
        Console.WriteLine($"Tables extracted    : {report.TablesExtracted}");
        Console.WriteLine($"Elapsed             : {report.Elapsed:mm\\:ss\\.f}");
        foreach (string warning in report.Warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }
    }

    /// <summary>Sprint 2 DoD checks: lineage groups and the doc-vector sanity cosines.</summary>
    private static async Task PrintDiagnosticsAsync(
        VectorStoreCollection<string, DocRecord> docCollection, CancellationToken ct)
    {
        var docs = new Dictionary<string, DocRecord>();
        await foreach (DocRecord doc in docCollection.GetAsync(_ => true, top: 100, cancellationToken: ct))
        {
            docs[doc.DocId] = doc;
        }

        Console.WriteLine();
        Console.WriteLine("=== Lineage groups ===");
        foreach (var group in docs.Values
                     .Where(d => d.LineageGroup is not null)
                     .GroupBy(d => d.LineageGroup)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{group.Key}: {string.Join(", ", group.Select(d => d.DocId).Order(StringComparer.Ordinal))}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Doc-vector sanity cosines ===");
        PrintCosine(docs, "A1", "A2"); // engineered pair: expect > 0.95
        PrintCosine(docs, "A1", "B3"); // cross-cluster: expect < 0.5
        PrintCosine(docs, "A1", "A3"); // same cluster, distinct: expect between
        PrintCosine(docs, "C4", "C5"); // engineered pair: expect > 0.95
        // Cluster D acceptance (§4: "a topic the instructor has not tuned against"): D must sit
        // far from A/B/C and cohere internally, or its recommender grading is meaningless.
        PrintCosine(docs, "D2", "A1"); // D vs regulation: expect < 0.5
        PrintCosine(docs, "D2", "B3"); // D vs RAG research: expect < 0.5
        PrintCosine(docs, "D2", "C1"); // D vs graph-RAG/eval: expect < 0.5
        PrintCosine(docs, "D2", "D5"); // within D (both zero-trust): expect high but < 0.95
        PrintCosine(docs, "D1", "D3"); // within D, distinct sub-topics: expect moderate
    }

    private static void PrintCosine(Dictionary<string, DocRecord> docs, string a, string b)
    {
        if (docs.TryGetValue(a, out DocRecord? docA) && docs.TryGetValue(b, out DocRecord? docB))
        {
            Console.WriteLine($"cos({a},{b}) = {LineageDetector.Cosine(docA.Embedding, docB.Embedding):F4}");
        }
    }
}
