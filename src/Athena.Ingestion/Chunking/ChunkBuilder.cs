using Athena.Core.Records;
using Athena.Ingestion.Extraction;

namespace Athena.Ingestion.Chunking;

/// <summary>
/// Shared, pure chunk-assembly helpers used by both chunkers: token-window slicing with page
/// attribution, table chunk emission, and ChunkRecord construction.
/// </summary>
internal static class ChunkBuilder
{
    /// <summary>A whitespace-delimited word with the page it came from and its token cost.</summary>
    internal readonly record struct PagedWord(string Word, int Page, int Tokens);

    internal static List<PagedWord> ToPagedWords(
        IEnumerable<(int Page, string Text)> pieces, ITokenCounter counter)
    {
        var words = new List<PagedWord>();
        foreach ((int page, string text) in pieces)
        {
            foreach (string word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                // Summing per-word token counts slightly overestimates a joined text's count
                // (merged word-boundary tokens are lost), which errs windows on the small side.
                // Acceptable: windows stay under the embedding model's limit by construction.
                words.Add(new PagedWord(word, page, counter.CountTokens(word)));
            }
        }

        return words;
    }

    /// <summary>
    /// Slices a word stream into windows of ~<paramref name="windowTokens"/> tokens with
    /// ~<paramref name="overlapTokens"/> tokens of trailing context repeated at the head of the
    /// next window. Returns each window's text and the pages of its first and last words.
    /// </summary>
    internal static List<(string Text, int StartPage, int EndPage)> SliceWindows(
        IReadOnlyList<PagedWord> words, int windowTokens, int overlapTokens)
    {
        var windows = new List<(string, int, int)>();
        int start = 0;
        while (start < words.Count)
        {
            int tokens = 0;
            int end = start;
            while (end < words.Count && tokens + words[end].Tokens <= windowTokens)
            {
                tokens += words[end].Tokens;
                end++;
            }

            // A single word larger than the window (pathological, but possible with long
            // OCR-mangled strings) still advances by one word to guarantee progress.
            if (end == start)
            {
                end = start + 1;
            }

            windows.Add((JoinWords(words, start, end), words[start].Page, words[end - 1].Page));

            if (end >= words.Count)
            {
                break;
            }

            // Back off from the window's end until ~overlapTokens are repeated, but always
            // advance past the previous start so the loop terminates.
            int overlap = 0;
            int nextStart = end;
            while (nextStart > start + 1 && overlap + words[nextStart - 1].Tokens <= overlapTokens)
            {
                nextStart--;
                overlap += words[nextStart].Tokens;
            }

            start = nextStart;
        }

        return windows;
    }

    private static string JoinWords(IReadOnlyList<PagedWord> words, int start, int end)
    {
        var parts = new string[end - start];
        for (int i = start; i < end; i++)
        {
            parts[i - start] = words[i].Word;
        }

        return string.Join(' ', parts);
    }

    internal static ChunkRecord CreateProse(
        ExtractedDocument document, int sequence, string text, int startPage, int endPage, string section)
        => Create(document, sequence, text, startPage, endPage, section,
            // Per-page OCR awareness (design rule 9): a chunk inherits the classification of
            // the page it starts on. Chunks spanning a native/OCR page boundary are attributed
            // to their start page -- same convention as PageNumber itself.
            document.PageKinds.TryGetValue(startPage, out ChunkKind kind) ? kind : ChunkKind.Prose);

    /// <summary>Tables are always their own chunks and are never split (brief rule).</summary>
    internal static IEnumerable<ChunkRecord> CreateTableChunks(
        ExtractedDocument document, int firstSequence)
    {
        int sequence = firstSequence;
        foreach (PageTable table in document.Tables.OrderBy(t => t.PageNumber))
        {
            yield return Create(document, sequence++, table.MarkdownTable, table.PageNumber,
                table.PageNumber, section: string.Empty, ChunkKind.Table);
        }
    }

    private static ChunkRecord Create(
        ExtractedDocument document, int sequence, string text, int page, int endPage,
        string section, ChunkKind kind)
        => new()
        {
            // Deterministic id: same extraction in => same ids out, so re-indexing upserts
            // rather than duplicating.
            ChunkId = $"{document.Meta.DocId}-{sequence:D4}",
            Text = text,
            DocId = document.Meta.DocId,
            Title = document.Meta.Title,
            PageNumber = page,
            EndPage = endPage,
            Section = section,
            Cluster = document.Meta.Cluster,
            PublishedOn = document.Meta.PublishedOn,
            Kind = kind,
        };
}
