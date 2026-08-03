namespace Athena.Ingestion.Extraction;

public readonly record struct PageText(int PageNumber, string Text, float MeanConfidence);

public interface IPdfTextExtractor
{
    Task<IReadOnlyList<PageText>> ExtractAsync(string pdfPath, CancellationToken ct = default);
}
