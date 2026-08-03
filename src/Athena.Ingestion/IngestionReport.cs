namespace Athena.Ingestion;

/// <summary>What one ingestion run did — the brief's §6.5 record, verbatim.</summary>
public sealed record IngestionReport(
    int DocumentsProcessed,
    int ChunksWritten,
    int PagesOcrd,
    int TablesExtracted,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);
