using Docnet.Core;
using Docnet.Core.Models;

namespace Athena.Ingestion.Extraction;

/// <summary>Per-page text-layer presence for a PDF (1-based page numbers).</summary>
public interface ITextLayerProbe
{
    IReadOnlyDictionary<int, bool> Probe(string pdfPath);
}

/// <summary>
/// The brief's §6.2 routing signal, faithfully translated: "if PdfPig yields under ~50 chars
/// for a page, that page goes to OCR" — here, if the PDF's native text layer yields under
/// 50 chars for a page, that page's DI text is image-OCR-derived (ChunkKind.OcrProse).
///
/// WHY this exists (measured on the real corpus, 2026-07-30): Azure DI's output carries NO
/// signal separating a clean 200-DPI raster from a native page — A1-SCAN averaged word
/// confidence 0.9913 vs native A1's 0.9902, identical tails, 4071/4074 words recovered. The
/// confidence-threshold classifier the IDP deviation originally proposed is therefore
/// undecidable on exactly the artifact it must classify, and text-layer presence (design
/// rule 9's sanctioned signal) is the one deterministic discriminator available.
///
/// Docnet's role: this probe reads each page's text ONLY to measure its length — the text is
/// discarded, never stored, never chunked, never embedded. Extraction remains Azure DI end to
/// end (README "Deviations" documents this narrow widening of Docnet's manufacture-only role).
/// </summary>
public sealed class DocnetTextLayerProbe : ITextLayerProbe
{
    // The brief's own number: a page whose native extractor yields under ~50 chars has no
    // usable text layer. Whitespace is stripped first so a "text layer" of layout artifacts
    // (stray spaces, newlines) does not count as text.
    private const int MinTextLayerChars = 50;

    public IReadOnlyDictionary<int, bool> Probe(string pdfPath)
    {
        var result = new Dictionary<int, bool>();
        // Probe-scale dimensions: PDFium requires page dimensions to open a document, but
        // GetText reads the text layer, which is resolution-independent — 1.0 keeps it cheap.
        using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
        int pageCount = docReader.GetPageCount();
        for (int i = 0; i < pageCount; i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            string text = pageReader.GetText() ?? string.Empty;
            int chars = text.Count(c => !char.IsWhiteSpace(c));
            result[i + 1] = chars >= MinTextLayerChars;
        }

        return result;
    }
}
