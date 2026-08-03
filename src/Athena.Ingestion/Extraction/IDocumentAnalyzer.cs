using Azure.AI.DocumentIntelligence;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// This is the only interface whose implementation may call Azure Document Intelligence
/// directly (construct a <see cref="DocumentIntelligenceClient"/>, call
/// <c>AnalyzeDocumentAsync</c>). It calls <c>prebuilt-layout</c> ONCE per document ever,
/// caching the raw JSON response, so repeated ingestion runs read cache instead of paying for
/// or waiting on the network call (design rule 11).
/// </summary>
public interface IDocumentAnalyzer
{
    Task<AnalyzeResult> AnalyzeAsync(string pdfPath, CancellationToken ct = default);
}
