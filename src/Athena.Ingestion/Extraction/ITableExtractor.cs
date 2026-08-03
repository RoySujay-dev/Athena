namespace Athena.Ingestion.Extraction;

/// <summary>One extracted table, serialised to Markdown, with the page it appears on.</summary>
public readonly record struct PageTable(int PageNumber, string MarkdownTable);

/// <summary>
/// The brief's table-extraction seam (§6.2). Deviation from the brief's skeleton, recorded in
/// README "Deviations": the brief declares a synchronous
/// <c>IReadOnlyList&lt;(int, string)&gt; Extract(string pdfPath)</c>, but in this build tables
/// come from the cached Azure DI analysis — an I/O read (and, on first ingestion, a network
/// call) — so the method is async with a CancellationToken, per the repo's async-throughout
/// rule. The named record struct replaces the anonymous tuple for the same reason PageText is
/// a record struct.
/// </summary>
public interface ITableExtractor
{
    Task<IReadOnlyList<PageTable>> ExtractAsync(string pdfPath, CancellationToken ct = default);
}
