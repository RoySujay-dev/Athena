using System.Diagnostics;
using Athena.Core.Records;
using Athena.Ingestion.Chunking;
using Athena.Ingestion.DocVectors;
using Athena.Ingestion.Enrichment;
using Athena.Ingestion.Extraction;
using Athena.Ingestion.Lineage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace Athena.Ingestion;

/// <summary>
/// PDF → ChunkRecord / DocRecord, per the brief's §6.5 shape. Every per-document step reads
/// the ONE cached DI analysis (hard constraint 11): text, paragraphs, tables and page
/// classification are all views over the same response, and with warm DI/summary/embedding
/// caches a full re-index touches no network at all — that is what keeps it under two minutes.
/// After all documents are ingested, lineage groups are computed from signals and written back
/// (never hand-authored, hard constraint 2).
/// </summary>
public sealed class IngestionPipeline
{
    private readonly IPdfTextExtractor _textExtractor;
    private readonly IParagraphExtractor _paragraphExtractor;
    private readonly ITableExtractor _tableExtractor;
    private readonly ITextLayerProbe _textLayerProbe;
    private readonly IChunker _chunker;
    private readonly IDocumentSummariser _summariser;
    private readonly IDocumentVectorStrategy _docVectorStrategy;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly VectorStoreCollection<string, ChunkRecord> _chunkCollection;
    private readonly VectorStoreCollection<string, DocRecord> _docCollection;
    private readonly ILogger<IngestionPipeline> _logger;

    public IngestionPipeline(
        IPdfTextExtractor textExtractor,
        IParagraphExtractor paragraphExtractor,
        ITableExtractor tableExtractor,
        ITextLayerProbe textLayerProbe,
        IChunker chunker,
        IDocumentSummariser summariser,
        IDocumentVectorStrategy docVectorStrategy,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        VectorStoreCollection<string, ChunkRecord> chunkCollection,
        VectorStoreCollection<string, DocRecord> docCollection,
        ILogger<IngestionPipeline> logger)
    {
        _textExtractor = textExtractor;
        _paragraphExtractor = paragraphExtractor;
        _tableExtractor = tableExtractor;
        _textLayerProbe = textLayerProbe;
        _chunker = chunker;
        _summariser = summariser;
        _docVectorStrategy = docVectorStrategy;
        _embeddingGenerator = embeddingGenerator;
        _chunkCollection = chunkCollection;
        _docCollection = docCollection;
        _logger = logger;
    }

    /// <param name="docIds">
    /// Manifest ids to ingest, or null for the whole manifest. A document's manufactured
    /// scanned variant always travels with it. Partial runs are additive: existing documents
    /// stay in the collections and lineage is recomputed over the union, so ingesting A1
    /// today and A2 tomorrow still lands them in one lineage group.
    /// </param>
    public async Task<IngestionReport> RunAsync(string corpusDirectory, CancellationToken ct = default,
                                                IReadOnlyCollection<string>? docIds = null)
    {
        // The report's Elapsed is part of the brief's §6.5 record, so the pipeline times its
        // own single run at this boundary. Fine-grained latency instrumentation belongs in SK
        // filters (hard constraint 10), not scattered Stopwatches — this is the one exception
        // the report shape itself demands.
        var stopwatch = Stopwatch.StartNew();

        var manifest = await CorpusManifest.LoadAsync(
            Path.Combine(corpusDirectory, "manifest.json"), ct);

        var warnings = new List<string>();
        var docRecords = new List<DocRecord>();
        var docChunks = new Dictionary<string, IReadOnlyList<ChunkRecord>>();
        int pagesOcrd = 0, tablesExtracted = 0, chunksWritten = 0;

        await _chunkCollection.EnsureCollectionExistsAsync(ct);
        await _docCollection.EnsureCollectionExistsAsync(ct);

        foreach ((DocumentMetadata meta, string pdfPath) in EnumerateDocuments(manifest, corpusDirectory, warnings, docIds))
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Ingesting {DocId} from {Path}", meta.DocId, Path.GetFileName(pdfPath));

            IReadOnlyList<PageText> pages = await _textExtractor.ExtractAsync(pdfPath, ct);
            IReadOnlyList<PageParagraph> paragraphs = await _paragraphExtractor.ExtractAsync(pdfPath, ct);
            IReadOnlyList<PageTable> tables = await _tableExtractor.ExtractAsync(pdfPath, ct);

            // Per-page OCR classification (rule 9): DI OCRs every page in one pass, so instead
            // of routing pages to a second extractor we classify each page — primarily by
            // text-layer presence (the brief's §6.2 signal; DI confidence measured
            // indistinguishable on clean rasters), with confidence as a secondary tell.
            // This is where IngestionReport.PagesOcrd comes from.
            IReadOnlyDictionary<int, bool> textLayer = _textLayerProbe.Probe(pdfPath);
            var pageKinds = pages.ToDictionary(
                p => p.PageNumber,
                p => PageOcrClassifier.Classify(
                    textLayer.GetValueOrDefault(p.PageNumber, true), p.MeanConfidence));
            pagesOcrd += pageKinds.Values.Count(k => k == ChunkKind.OcrProse);
            tablesExtracted += tables.Count;

            DocumentMetadata metaWithPages = meta with { PageCount = pages.Count };
            IReadOnlyList<ChunkRecord> chunks = _chunker.Chunk(
                new ExtractedDocument(metaWithPages, pages, paragraphs, tables, pageKinds));
            if (chunks.Count == 0)
            {
                warnings.Add($"{meta.DocId}: produced no chunks; document skipped.");
                continue;
            }

            await EmbedChunksAsync(chunks, ct);

            string fullText = string.Join("\n", pages.Select(p => p.Text));
            DocumentSummary summary = await _summariser.SummariseAsync(metaWithPages, fullText, ct);

            var doc = new DocRecord
            {
                DocId = meta.DocId,
                Title = meta.Title,
                Cluster = meta.Cluster,
                PublishedOn = meta.PublishedOn,
                PageCount = pages.Count,
                Summary = summary.Summary,
                Topics = summary.Topics,
                ChunkCount = chunks.Count,
            };
            doc.Embedding = await _docVectorStrategy.BuildAsync(doc, chunks, ct);

            await _chunkCollection.UpsertAsync(chunks, ct);
            chunksWritten += chunks.Count;
            docRecords.Add(doc);
            docChunks[doc.DocId] = chunks;
        }

        // Lineage is computed over the finished doc vectors, so it runs after every document
        // is ingested; the groups are written back before the records are upserted. It runs
        // over the UNION of this batch and documents already in the collection — a partial
        // (docIds-filtered) run must not compute lineage blind to a sibling ingested earlier,
        // and for a full run over fresh collections the union is just the batch.
        var lineageDocs = new List<DocRecord>(docRecords);
        var batchIds = docRecords.Select(d => d.DocId).ToHashSet(StringComparer.Ordinal);
        await foreach (DocRecord existing in _docCollection.GetAsync(_ => true, top: int.MaxValue,
                           cancellationToken: ct))
        {
            if (!batchIds.Contains(existing.DocId))
            {
                lineageDocs.Add(existing);
            }
        }

        AssignLineageGroups(lineageDocs);
        await _docCollection.UpsertAsync(lineageDocs, ct);

        stopwatch.Stop();
        return new IngestionReport(
            DocumentsProcessed: docRecords.Count,
            ChunksWritten: chunksWritten,
            PagesOcrd: pagesOcrd,
            TablesExtracted: tablesExtracted,
            Warnings: warnings,
            Elapsed: stopwatch.Elapsed);
    }

    /// <summary>
    /// Manifest documents plus any manufactured scanned variants. A file named
    /// <c>{id}-scanned.pdf</c> beside a manifest document is ingested as DocId <c>{ID}-SCAN</c>
    /// with its source's metadata — discovered from the filesystem, not from a hardcoded list,
    /// so any document rasterised in future is picked up the same way.
    /// </summary>
    private static IEnumerable<(DocumentMetadata Meta, string PdfPath)> EnumerateDocuments(
        CorpusManifest manifest, string corpusDirectory, List<string> warnings,
        IReadOnlyCollection<string>? docIds)
    {
        foreach (ManifestEntry entry in manifest.Documents)
        {
            if (docIds is not null && !docIds.Contains(entry.Id))
            {
                continue;
            }

            string path = Path.Combine(corpusDirectory, $"{entry.Id}.pdf");
            if (!File.Exists(path))
            {
                warnings.Add($"{entry.Id}: {path} not found; run 'fetch' first. Skipped.");
                continue;
            }

            yield return (new DocumentMetadata(
                entry.Id, entry.Title, entry.Cluster, entry.PublishedOn, entry.ExpectedPages), path);

            string scannedPath = Path.Combine(corpusDirectory, $"{entry.Id}-scanned.pdf");
            if (File.Exists(scannedPath))
            {
                yield return (new DocumentMetadata(
                    $"{entry.Id}-SCAN",
                    $"{entry.Title} (scanned copy)",
                    entry.Cluster,
                    entry.PublishedOn,
                    entry.ExpectedPages), scannedPath);
            }
        }
    }

    private async Task EmbedChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        // One batched call per document keeps request counts low; per-document chunk counts
        // (tens, not thousands) stay well inside OpenAI batch limits.
        GeneratedEmbeddings<Embedding<float>> embeddings = await _embeddingGenerator.GenerateAsync(
            chunks.Select(c => c.Text), cancellationToken: ct);
        if (embeddings.Count != chunks.Count)
        {
            throw new InvalidOperationException(
                $"Got {embeddings.Count} embeddings for {chunks.Count} chunks.");
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = embeddings[i].Vector;
        }
    }

    private void AssignLineageGroups(List<DocRecord> docRecords)
    {
        var signals = docRecords
            .Select(d => new DocLineageSignals(d.DocId, d.Title, d.Cluster, d.PublishedOn, d.Embedding))
            .ToList();
        IReadOnlyDictionary<string, string> groups = LineageDetector.AssignGroups(signals);

        foreach (DocRecord doc in docRecords)
        {
            if (groups.TryGetValue(doc.DocId, out string? group))
            {
                doc.LineageGroup = group;
                _logger.LogInformation("Lineage: {DocId} -> group {Group}", doc.DocId, group);
            }
        }
    }
}
