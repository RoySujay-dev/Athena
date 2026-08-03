using Microsoft.Extensions.VectorData;

namespace Athena.Core.Records;

/// <summary>
/// One document of the corpus. Recommendation retrieval operates at this granularity — the
/// brief leaves this record to us (§6.1) but requires DocId, Title, Cluster, PublishedOn,
/// PageCount, Summary, Topics, LineageGroup and Embedding at minimum.
/// </summary>
public sealed class DocRecord
{
    [VectorStoreKey]
    public string DocId { get; set; } = string.Empty;

    [VectorStoreData]
    public string Title { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string Cluster { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public DateTimeOffset PublishedOn { get; set; }

    [VectorStoreData]
    public int PageCount { get; set; }

    /// <summary>LLM-generated abstract, ≤150 words.</summary>
    [VectorStoreData]
    public string Summary { get; set; } = string.Empty;

    /// <summary>3–6 LLM-extracted topic tags.</summary>
    [VectorStoreData]
    public IReadOnlyList<string> Topics { get; set; } = [];

    /// <summary>
    /// Version-lineage group id, or null for a document with no detected siblings. COMPUTED at
    /// ingestion by LineageDetector from doc-vector cosine + title similarity + cluster + dates
    /// (hard constraint 2) — never hand-authored, never a DocId exclusion list.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string? LineageGroup { get; set; }

    /// <summary>
    /// Number of chunks this document produced. Carried so the recommender can normalise
    /// chunk-hit aggregation by sqrt(chunkCount) — a 30-page doc has ~6x more chances to be
    /// hit than a 3-page one (length-bias, documented corpus trait).
    /// </summary>
    [VectorStoreData]
    public int ChunkCount { get; set; }

    /// <summary>Built by the configured IDocumentVectorStrategy (§6.4) — centroid or composite.</summary>
    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
