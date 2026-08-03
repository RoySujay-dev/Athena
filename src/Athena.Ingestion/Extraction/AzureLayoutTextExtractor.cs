using Azure.AI.DocumentIntelligence;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// Adapts a cached/single-shot <see cref="IDocumentAnalyzer"/> result into the brief's
/// <see cref="IPdfTextExtractor"/> seam. Depends on the interface, not the concrete
/// Azure-calling type, so it stays swappable and testable without any network access.
/// </summary>
public sealed class AzureLayoutTextExtractor : IPdfTextExtractor
{
    private readonly IDocumentAnalyzer _analyzer;

    public AzureLayoutTextExtractor(IDocumentAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    public async Task<IReadOnlyList<PageText>> ExtractAsync(string pdfPath, CancellationToken ct = default)
    {
        AnalyzeResult result = await _analyzer.AnalyzeAsync(pdfPath, ct);

        var pages = new List<PageText>(result.Pages.Count);
        foreach (DocumentPage page in result.Pages)
        {
            // DI's lines are already in page reading order, so joining them is the page's prose.
            // Words exist only to drive the confidence classification below -- reconstructing
            // text from them would throw away DI's own line/reading-order assembly. Each line's
            // :formula: placeholders are restored to LaTeX from the page's formulas add-on data.
            IReadOnlyList<DocumentFormula> formulas = page.Formulas ?? [];
            string text = string.Join(Environment.NewLine,
                page.Lines.Select(l => FormulaRestorer.Restore(l.Content, l.Spans, formulas)));

            // A genuinely blank page has zero words and nothing to be unconfident about --
            // treat it as native (1f) rather than divide-by-zero or misclassify it as OcrProse.
            float meanConfidence = page.Words.Count == 0
                ? 1f
                : page.Words.Select(w => w.Confidence).Average();

            pages.Add(new PageText(page.PageNumber, text, meanConfidence));
        }

        return pages.OrderBy(p => p.PageNumber).ToList();
    }
}
