using Athena.Core.Records;

namespace Athena.Ingestion.Chunking;

/// <summary>
/// The brief's §6.3 baseline: ~800-token windows with 15% (120-token) overlap over the page
/// text stream, blind to document structure. 800 tokens keeps a window comfortably inside the
/// embedding model's context while being large enough that a BCBS principle or a paper
/// subsection usually fits in one piece; 15% overlap is enough to keep a sentence split by a
/// window boundary fully present in one of its two windows without materially inflating index
/// size.
/// </summary>
public sealed class FixedWindowChunker : IChunker
{
    private readonly ITokenCounter _tokenCounter;
    private readonly int _windowTokens;
    private readonly int _overlapTokens;

    public FixedWindowChunker(ITokenCounter tokenCounter, int windowTokens = 800, int? overlapTokens = null)
    {
        _tokenCounter = tokenCounter;
        _windowTokens = windowTokens;
        _overlapTokens = overlapTokens ?? (int)(windowTokens * 0.15);
    }

    public string Name => "fixed";

    public IReadOnlyList<ChunkRecord> Chunk(ExtractedDocument document)
    {
        List<ChunkBuilder.PagedWord> words = ChunkBuilder.ToPagedWords(
            document.Pages.OrderBy(p => p.PageNumber).Select(p => (p.PageNumber, p.Text)),
            _tokenCounter);

        var chunks = new List<ChunkRecord>();
        foreach ((string text, int startPage, int endPage) in ChunkBuilder.SliceWindows(words, _windowTokens, _overlapTokens))
        {
            // Fixed windows have no section notion; Section stays empty by design (the
            // section-aware chunker is the one that pays for structure).
            chunks.Add(ChunkBuilder.CreateProse(document, chunks.Count, text, startPage, endPage, section: string.Empty));
        }

        chunks.AddRange(ChunkBuilder.CreateTableChunks(document, chunks.Count));
        return chunks;
    }
}
