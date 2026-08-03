using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// Reads DI's structured tables from the SAME cached analysis the text extractor uses — the
/// <see cref="IDocumentAnalyzer"/> is the single cache-backed gateway, so this never triggers
/// a second DI call for a document (design rule 11). Serialisation itself is delegated to
/// the pure <see cref="TableMarkdownSerializer"/>.
/// </summary>
public sealed class AzureTableExtractor : ITableExtractor
{
    private readonly IDocumentAnalyzer _analyzer;
    private readonly ILogger<AzureTableExtractor> _logger;

    public AzureTableExtractor(IDocumentAnalyzer analyzer, ILogger<AzureTableExtractor>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger ?? NullLogger<AzureTableExtractor>.Instance;
    }

    public async Task<IReadOnlyList<PageTable>> ExtractAsync(string pdfPath, CancellationToken ct = default)
    {
        var result = await _analyzer.AnalyzeAsync(pdfPath, ct);

        var tables = new List<PageTable>();
        for (var i = 0; i < result.Tables.Count; i++)
        {
            DocumentTable table = result.Tables[i];

            var pageNumber = ResolvePageNumber(table);
            if (pageNumber is null)
            {
                // A citation-less table is worthless downstream (page numbers are non-negotiable
                // for citations), so skip rather than emit an unlocatable table.
                _logger.LogWarning(
                    "Table {Index} in '{Pdf}' has no bounding region on any page; skipping it.",
                    i, pdfPath);
                continue;
            }

            var cells = table.Cells
                .Select(c => new TableCellData(
                    c.RowIndex,
                    c.ColumnIndex,
                    c.RowSpan ?? 1,
                    c.ColumnSpan ?? 1,
                    c.Content ?? string.Empty))
                .ToList();

            var markdown = TableMarkdownSerializer.Serialize(table.RowCount, table.ColumnCount, cells);
            if (markdown is null)
            {
                // Degenerate table (DI sometimes detects a "table" in dense prose or rules
                // lines): warn and skip rather than pollute the chunk stream with garbage.
                _logger.LogWarning(
                    "Table {Index} on page {Page} of '{Pdf}' is degenerate ({Rows}x{Cols}, {CellCount} cells); skipping it.",
                    i, pageNumber, pdfPath, table.RowCount, table.ColumnCount, table.Cells.Count);
                continue;
            }

            tables.Add(new PageTable(pageNumber.Value, markdown));
        }

        return tables;
    }

    /// <summary>
    /// A table's page is its first bounding region's page; if DI attached none to the table
    /// itself, fall back to the first cell that carries one (a multi-page table is attributed
    /// to its starting page — where a citation would point a reader).
    /// </summary>
    private static int? ResolvePageNumber(DocumentTable table)
    {
        if (table.BoundingRegions.Count > 0)
            return table.BoundingRegions[0].PageNumber;

        foreach (var cell in table.Cells)
        {
            if (cell.BoundingRegions.Count > 0)
                return cell.BoundingRegions[0].PageNumber;
        }

        return null;
    }
}
