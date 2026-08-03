using Athena.Core.Records;
using Microsoft.Extensions.VectorData;

namespace Athena.Retrieval;

/// <summary>
/// Location-addressed retrieval: the chunks covering a page range of one document, in page
/// order. Similarity search cannot serve "page 16 of B1" — a page number has no meaning in
/// embedding space and near-none in BM25 (observed live: 'how many names on page 16, refs
/// 66-68' retrieved nothing usable and the agent abstained). This is the complement, not a
/// replacement: no query, no ranking, just the document's own text at a named location.
/// </summary>
public sealed class PageReader
{
    /// <summary>
    /// Cap on pages per read: bounds the tokens handed to the model (a page is roughly 500
    /// tokens of prose) while still covering any plausible "pages N-M" ask.
    /// </summary>
    public const int MaxPageSpan = 5;

    private readonly VectorStoreCollection<string, ChunkRecord> _chunks;

    public PageReader(VectorStoreCollection<string, ChunkRecord> chunks) => _chunks = chunks;

    public async Task<IReadOnlyList<Passage>> ReadAsync(string docId, int fromPage, int toPage,
                                                        CancellationToken ct = default)
    {
        var docChunks = new List<ChunkRecord>();
        await foreach (ChunkRecord chunk in _chunks.GetAsync(
                           c => c.DocId == docId, top: int.MaxValue, cancellationToken: ct))
        {
            docChunks.Add(chunk);
        }

        return SelectPages(docChunks, fromPage, toPage)
            .Select(c => new Passage(c.ChunkId, c.DocId, c.Title, c.PageNumber, c.Text,
                Score: 0, c.EndPage))
            .ToList();
    }

    /// <summary>
    /// Chunks whose page span [PageNumber, EndPage] intersects the (normalised, capped)
    /// requested range, in page-then-id order. Pure — unit-tested directly.
    /// </summary>
    public static IReadOnlyList<ChunkRecord> SelectPages(
        IReadOnlyList<ChunkRecord> chunks, int fromPage, int toPage)
    {
        fromPage = Math.Max(1, fromPage);
        // toPage 0 (or below fromPage) means "just fromPage"; the span cap bounds cost.
        toPage = Math.Clamp(toPage < fromPage ? fromPage : toPage,
            fromPage, fromPage + MaxPageSpan - 1);

        return chunks
            .Where(c => c.PageNumber <= toPage && Math.Max(c.PageNumber, c.EndPage) >= fromPage)
            .OrderBy(c => c.PageNumber)
            .ThenBy(c => c.ChunkId, StringComparer.Ordinal)
            .ToList();
    }
}
