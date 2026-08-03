using Athena.Core.Records;

namespace Athena.Ingestion.Enrichment;

/// <summary>The LLM-derived enrichment of a document: a ≤150-word abstract and 3–6 topic tags.</summary>
public sealed record DocumentSummary(string Summary, IReadOnlyList<string> Topics);

/// <summary>
/// Produces <see cref="DocRecord.Summary"/> and <see cref="DocRecord.Topics"/> (brief §6.1).
/// Wraps a prompt function per §6.5; the pipeline treats it as a black box.
/// </summary>
public interface IDocumentSummariser
{
    Task<DocumentSummary> SummariseAsync(
        DocumentMetadata meta, string documentText, CancellationToken ct = default);
}
