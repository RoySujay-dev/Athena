using Athena.Core.Records;
using Athena.Ingestion.Extraction;

namespace Athena.Ingestion.Chunking;

/// <summary>
/// Splits on document structure: a new chunk starts at every DI-labelled Title/SectionHeading
/// paragraph, and any section above ~1,000 tokens is subdivided with the fixed-window logic
/// (brief §6.3).
///
/// Section detection trusts Azure DI's paragraph ROLES rather than hand-rolled heading
/// heuristics — the IDP-adapted path (README "Deviations" §1). That label mechanism covers both
/// corpus shapes the brief warns about: cluster A's numbered regulatory principles and cluster
/// B's two-column papers (whose reading order DI has already linearised). The cost is trusting
/// DI's labels — spot-checked on both clusters as part of the sprint's DoD.
/// </summary>
public sealed class SectionAwareChunker : IChunker
{
    private readonly ITokenCounter _tokenCounter;
    private readonly int _maxSectionTokens;
    private readonly int _windowTokens;
    private readonly int _overlapTokens;

    public SectionAwareChunker(
        ITokenCounter tokenCounter, int maxSectionTokens = 1000, int windowTokens = 800, int? overlapTokens = null)
    {
        _tokenCounter = tokenCounter;
        _maxSectionTokens = maxSectionTokens;
        _windowTokens = windowTokens;
        _overlapTokens = overlapTokens ?? (int)(windowTokens * 0.15);
    }

    public string Name => "section";

    public IReadOnlyList<ChunkRecord> Chunk(ExtractedDocument document)
    {
        var chunks = new List<ChunkRecord>();

        foreach (Section section in GroupIntoSections(document.Paragraphs))
        {
            List<ChunkBuilder.PagedWord> words = ChunkBuilder.ToPagedWords(
                section.Paragraphs.Select(p => (p.PageNumber, p.Text)), _tokenCounter);
            if (words.Count == 0)
            {
                continue;
            }

            int sectionTokens = words.Sum(w => w.Tokens);
            if (sectionTokens <= _maxSectionTokens)
            {
                string text = PrefixHeading(section.Heading,
                    string.Join(Environment.NewLine, section.Paragraphs.Select(p => p.Text)));
                chunks.Add(ChunkBuilder.CreateProse(
                    document, chunks.Count, text, section.StartPage,
                    section.Paragraphs.Max(p => p.PageNumber), section.Heading));
            }
            else
            {
                // Oversized section: subdivide with the fixed-window rules, but keep every
                // sub-chunk labelled with (and prefixed by) its section heading so retrieval
                // still sees the structural context.
                foreach ((string text, int startPage, int endPage) in ChunkBuilder.SliceWindows(
                    words, _windowTokens, _overlapTokens))
                {
                    chunks.Add(ChunkBuilder.CreateProse(
                        document, chunks.Count, PrefixHeading(section.Heading, text), startPage,
                        endPage, section.Heading));
                }
            }
        }

        chunks.AddRange(ChunkBuilder.CreateTableChunks(document, chunks.Count));
        return chunks;
    }

    private static string PrefixHeading(string heading, string body)
        => string.IsNullOrEmpty(heading) ? body : heading + Environment.NewLine + body;

    private sealed record Section(string Heading, int StartPage, IReadOnlyList<PageParagraph> Paragraphs);

    private static IEnumerable<Section> GroupIntoSections(IReadOnlyList<PageParagraph> paragraphs)
    {
        string heading = string.Empty;
        int startPage = paragraphs.Count > 0 ? paragraphs[0].PageNumber : 1;
        var body = new List<PageParagraph>();

        foreach (PageParagraph paragraph in paragraphs)
        {
            if (paragraph.Kind is ParagraphKind.Title or ParagraphKind.SectionHeading)
            {
                if (body.Count > 0)
                {
                    yield return new Section(heading, startPage, body);
                    body = [];
                }

                heading = paragraph.Text;
                startPage = paragraph.PageNumber;
            }
            else
            {
                if (body.Count == 0 && heading.Length == 0)
                {
                    // Preamble before any heading starts its own section at its own page.
                    startPage = paragraph.PageNumber;
                }

                body.Add(paragraph);
            }
        }

        if (body.Count > 0)
        {
            yield return new Section(heading, startPage, body);
        }
    }
}
