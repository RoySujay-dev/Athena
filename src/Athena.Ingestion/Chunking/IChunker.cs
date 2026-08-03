using Athena.Core.Records;
using Athena.Ingestion.Extraction;

namespace Athena.Ingestion.Chunking;

/// <summary>
/// Everything extraction learned about one document, bundled as the chunkers' input.
///
/// DEVIATION from the brief's §6.3 signature (documented in README): the brief passes
/// (meta, pages, tables). We add <see cref="Paragraphs"/> because section detection uses DI's
/// paragraph roles instead of the PdfPig bounding-box heuristics the brief assumed, and
/// <see cref="PageKinds"/> because the per-page OCR classification (design rule 9) must
/// flow into each chunk's <see cref="ChunkRecord.Kind"/>.
/// </summary>
public sealed record ExtractedDocument(
    DocumentMetadata Meta,
    IReadOnlyList<PageText> Pages,
    IReadOnlyList<PageParagraph> Paragraphs,
    IReadOnlyList<PageTable> Tables,
    IReadOnlyDictionary<int, ChunkKind> PageKinds);

/// <summary>
/// Pure document → chunks function: no I/O, no clock, no embedding — the pipeline fills
/// <see cref="ChunkRecord.Embedding"/> afterwards. Determinism matters: re-chunking the same
/// extraction must yield identical ChunkIds so re-indexing is stable.
/// </summary>
public interface IChunker
{
    string Name { get; }

    IReadOnlyList<ChunkRecord> Chunk(ExtractedDocument document);
}
