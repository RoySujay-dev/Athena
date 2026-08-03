using Athena.Core.Records;
using Athena.Ingestion.Chunking;
using Athena.Ingestion.Extraction;

namespace Athena.Tests.Chunking;

/// <summary>
/// One-token-per-word counter so chunker tests reason in word counts and need no cl100k
/// vocabulary data (the seam exists exactly for this).
/// </summary>
internal sealed class WordTokenCounter : ITokenCounter
{
    public int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal static class ChunkerTestSupport
{
    internal static readonly DocumentMetadata Meta = new(
        DocId: "T1",
        Title: "Test Document",
        Cluster: "A",
        PublishedOn: new DateTimeOffset(2021, 3, 31, 0, 0, 0, TimeSpan.Zero),
        PageCount: 2);

    internal static ExtractedDocument Document(
        IReadOnlyList<PageText>? pages = null,
        IReadOnlyList<PageParagraph>? paragraphs = null,
        IReadOnlyList<PageTable>? tables = null,
        IReadOnlyDictionary<int, ChunkKind>? pageKinds = null)
        => new(Meta, pages ?? [], paragraphs ?? [], tables ?? [], pageKinds ?? new Dictionary<int, ChunkKind>());

    /// <summary>"w1 w2 ... wN" — N distinct single-token words.</summary>
    internal static string Words(int count, string prefix = "w")
        => string.Join(' ', Enumerable.Range(1, count).Select(i => $"{prefix}{i}"));
}
