namespace Athena.Ingestion.Extraction;

/// <summary>
/// Structural role of a paragraph, mirroring Azure DI's layout roles. The section-aware
/// chunker keys on <see cref="Title"/> / <see cref="SectionHeading"/> instead of hand-rolled
/// heading heuristics — this is the IDP-adapted path (README "Deviations" §1): the brief's PdfPig build
/// would have inferred headings from font sizes and bounding boxes; DI labels them for us.
/// </summary>
public enum ParagraphKind
{
    Body,
    Title,
    SectionHeading,
    Footnote
}

/// <summary>A reading-ordered paragraph with the page it starts on and its structural role.</summary>
public readonly record struct PageParagraph(int PageNumber, string Text, ParagraphKind Kind);

/// <summary>
/// Extracts reading-ordered, role-labelled paragraphs. Sits beside <see cref="IPdfTextExtractor"/>
/// (which yields per-page joined text for the fixed-window path) because section-aware chunking
/// needs structure that flat page text has already thrown away.
/// </summary>
public interface IParagraphExtractor
{
    Task<IReadOnlyList<PageParagraph>> ExtractAsync(string pdfPath, CancellationToken ct = default);
}
