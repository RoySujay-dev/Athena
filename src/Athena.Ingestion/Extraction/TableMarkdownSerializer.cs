using System.Text;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// Engine-agnostic cell data for one table cell. Spans are pre-defaulted to 1 by the caller so
/// this type never deals in nullables.
/// </summary>
public readonly record struct TableCellData(
    int RowIndex,
    int ColumnIndex,
    int RowSpan,
    int ColumnSpan,
    string Content);

/// <summary>
/// Pure serialisation of a cell grid to a GitHub-flavoured Markdown table. No I/O, no Azure
/// types — unit-testable in isolation, mirroring how MMR/RRF/dedup stay pure elsewhere.
/// </summary>
public static class TableMarkdownSerializer
{
    /// <summary>
    /// Serialise to Markdown, or return null for a degenerate table (no rows, no columns, or
    /// no non-empty cell content) so the caller can warn and skip instead of emitting garbage —
    /// a table flattened into noise is worse downstream than no table at all.
    /// </summary>
    public static string? Serialize(int rowCount, int columnCount, IReadOnlyList<TableCellData> cells)
    {
        if (rowCount < 1 || columnCount < 1 || cells.Count == 0)
            return null;

        var grid = new string[rowCount, columnCount];

        foreach (var cell in cells)
        {
            // Markdown has no rowspan/colspan. Repeating the content into every covered cell
            // keeps rows and columns truthfully aligned (a header spanning three columns really
            // does label all three), which is what retrieval over the serialised text needs.
            // Cost: duplicated text slightly inflates chunk token counts; accepted.
            for (var r = cell.RowIndex; r < Math.Min(cell.RowIndex + cell.RowSpan, rowCount); r++)
            for (var c = cell.ColumnIndex; c < Math.Min(cell.ColumnIndex + cell.ColumnSpan, columnCount); c++)
                grid[r, c] = EscapeCell(cell.Content);
        }

        var anyContent = false;
        for (var r = 0; r < rowCount && !anyContent; r++)
        for (var c = 0; c < columnCount && !anyContent; c++)
            anyContent = !string.IsNullOrWhiteSpace(grid[r, c]);
        if (!anyContent)
            return null;

        var sb = new StringBuilder();

        // Row 0 becomes the Markdown header row. DI marks header cells (Kind=columnHeader) and
        // they sit in the leading row(s); for BCBS annex and paper results tables the first row
        // is the column-header row, which is exactly what Markdown's header line expects.
        AppendRow(sb, grid, 0, columnCount);

        sb.Append('|');
        for (var c = 0; c < columnCount; c++)
            sb.Append(" --- |");
        sb.AppendLine();

        for (var r = 1; r < rowCount; r++)
            AppendRow(sb, grid, r, columnCount);

        return sb.ToString().TrimEnd();
    }

    private static void AppendRow(StringBuilder sb, string[,] grid, int row, int columnCount)
    {
        sb.Append('|');
        for (var c = 0; c < columnCount; c++)
        {
            sb.Append(' ').Append(grid[row, c] ?? string.Empty).Append(" |");
        }
        sb.AppendLine();
    }

    private static string EscapeCell(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        // Pipes would break the column structure; newlines would break the row structure.
        return content
            .Replace("|", "\\|")
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
    }
}
