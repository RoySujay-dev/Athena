using Azure.AI.DocumentIntelligence;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// Adapts the cached DI analysis into role-labelled paragraphs. Reads the SAME cached
/// <see cref="AnalyzeResult"/> as the text and table extractors via <see cref="IDocumentAnalyzer"/> —
/// never a second DI call per document (design constraint 11).
/// </summary>
public sealed class AzureLayoutParagraphExtractor : IParagraphExtractor
{
    private readonly IDocumentAnalyzer _analyzer;

    public AzureLayoutParagraphExtractor(IDocumentAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    public async Task<IReadOnlyList<PageParagraph>> ExtractAsync(string pdfPath, CancellationToken ct = default)
    {
        AnalyzeResult result = await _analyzer.AnalyzeAsync(pdfPath, ct);

        // DI returns paragraphs in reading order, which for cluster B's two-column papers means
        // the column interleaving problem is already solved upstream -- we deliberately do NOT
        // re-sort by geometry here. Preserving DI's order is the point (README "Deviations" §1).
        // Formula spans are global content offsets, so one pooled list serves every paragraph.
        var allFormulas = result.Pages
            .SelectMany(p => p.Formulas ?? Enumerable.Empty<DocumentFormula>())
            .ToList();
        var paragraphs = new List<PageParagraph>(result.Paragraphs.Count);
        foreach (DocumentParagraph paragraph in result.Paragraphs)
        {
            // Running headers, footers, and bare page numbers are page furniture: they repeat on
            // every page, carry no retrievable content, and would pollute chunk text and BM25
            // term statistics. Drop them at the seam.
            if (paragraph.Role == ParagraphRole.PageHeader
                || paragraph.Role == ParagraphRole.PageFooter
                || paragraph.Role == ParagraphRole.PageNumber)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(paragraph.Content))
            {
                continue;
            }

            ParagraphKind kind =
                paragraph.Role == ParagraphRole.Title ? ParagraphKind.Title
                : paragraph.Role == ParagraphRole.SectionHeading ? ParagraphKind.SectionHeading
                : paragraph.Role == ParagraphRole.Footnote ? ParagraphKind.Footnote
                // Unlabelled paragraphs and formula blocks are body text.
                : ParagraphKind.Body;

            // Paragraphs can span pages; attribute each to the page it starts on, consistent
            // with ChunkRecord.PageNumber being the chunk's start page.
            int pageNumber = paragraph.BoundingRegions.Count > 0
                ? paragraph.BoundingRegions[0].PageNumber
                : 1;

            paragraphs.Add(new PageParagraph(pageNumber,
                FormulaRestorer.Restore(paragraph.Content, paragraph.Spans, allFormulas), kind));
        }

        return paragraphs;
    }
}
