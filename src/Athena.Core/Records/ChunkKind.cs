namespace Athena.Core.Records;

/// <summary>
/// The kind of content a chunk represents, matching the brief's <c>ChunkRecord.Kind</c> field
/// (§6.1) that Sprint 2 will introduce. <see cref="OcrProse"/> distinguishes prose recovered
/// from an image-OCR-derived page (per-page confidence classification, see
/// Athena.Ingestion.Extraction.PageOcrClassifier) from ordinary <see cref="Prose"/> and
/// <see cref="Table"/> content. Athena.Core stays dependency-free -- this is a plain enum.
/// </summary>
public enum ChunkKind
{
    Prose,
    Table,
    OcrProse
}
