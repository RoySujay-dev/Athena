using Microsoft.Extensions.VectorData;

namespace Athena.Core.Records;

/// <summary>
/// One retrievable unit of a document, per the brief's §6.1 skeleton. QA retrieval operates at
/// this granularity. <see cref="PageNumber"/> threads losslessly from Azure DI extraction —
/// citations without a page number score zero (hard constraint 7).
/// </summary>
public sealed class ChunkRecord
{
    [VectorStoreKey]
    public string ChunkId { get; set; } = string.Empty;

    [VectorStoreData(IsFullTextIndexed = true)]
    public string Text { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string DocId { get; set; } = string.Empty;

    [VectorStoreData]
    public string Title { get; set; } = string.Empty;

    /// <summary>Page on which this chunk starts. Citations without this score zero.</summary>
    [VectorStoreData(IsIndexed = true)]
    public int PageNumber { get; set; }

    /// <summary>
    /// Last page this chunk's text touches (== PageNumber for single-page chunks). An
    /// ~800-token window spans roughly two pages, so content the model cites may genuinely
    /// sit after the start page — the grounding guard validates cited pages against the
    /// [PageNumber, EndPage] span instead of the start page alone.
    /// </summary>
    [VectorStoreData]
    public int EndPage { get; set; }

    /// <summary>Nearest preceding heading (section-aware chunking) or empty.</summary>
    [VectorStoreData]
    public string Section { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string Cluster { get; set; } = string.Empty; // "A" | "B" | "C" | "D"

    [VectorStoreData(IsIndexed = true)]
    public DateTimeOffset PublishedOn { get; set; }

    [VectorStoreData]
    public ChunkKind Kind { get; set; } // Prose | Table | OcrProse

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
