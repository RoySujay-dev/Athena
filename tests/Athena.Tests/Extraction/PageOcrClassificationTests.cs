using System.ClientModel.Primitives;
using System.Security.Cryptography;
using Athena.Core.Records;
using Athena.Ingestion.Extraction;
using Azure;
using Azure.AI.DocumentIntelligence;

namespace Athena.Tests.Extraction;

/// <summary>
/// End-to-end through IDocumentAnalyzer + AzureLayoutTextExtractor + PageOcrClassifier: one
/// text-layer page and one image-only page, exactly one classifies as OcrProse. The fixture
/// encodes the MEASURED corpus reality (2026-07-30): DI reports ~0.99 confidence on clean
/// rasters and native pages alike, so the text-layer probe — not confidence — is what
/// separates them.
/// </summary>
public class PageOcrClassificationTests
{
    [Fact]
    public async Task ExtractAsync_ClassifiesExactlyOnePageAsOcrProse_WhenOnePageIsImageOnly()
    {
        string tempDir = Directory.CreateTempSubdirectory("athena-di-cache-").FullName;
        string tempPdfPath = Path.Combine(tempDir, "fixture.pdf");

        try
        {
            // BOTH pages carry the high confidence DI actually reports (native A1 measured
            // 0.9902, rasterised A1-SCAN 0.9913 — indistinguishable). The probe result below
            // is the only signal that differs, as in the real corpus.
            DocumentPage nativePage = DocumentIntelligenceModelFactory.DocumentPage(
                pageNumber: 1,
                words:
                [
                    DocumentIntelligenceModelFactory.DocumentWord(content: "Native", confidence: 0.992f),
                    DocumentIntelligenceModelFactory.DocumentWord(content: "text.", confidence: 0.989f),
                ],
                lines: [DocumentIntelligenceModelFactory.DocumentLine(content: "Native text.")]);

            DocumentPage scannedPage = DocumentIntelligenceModelFactory.DocumentPage(
                pageNumber: 2,
                words:
                [
                    DocumentIntelligenceModelFactory.DocumentWord(content: "Scanned", confidence: 0.991f),
                    DocumentIntelligenceModelFactory.DocumentWord(content: "text.", confidence: 0.993f),
                ],
                lines: [DocumentIntelligenceModelFactory.DocumentLine(content: "Scanned text.")]);

            AnalyzeResult fakeResult = DocumentIntelligenceModelFactory.AnalyzeResult(
                content: "Native text.\nScanned text.",
                pages: [nativePage, scannedPage]);

            // Force a cache hit so the analyzer never touches Azure (fast, credential-free).
            byte[] pdfBytes = "not a real pdf -- content is never parsed, only hashed"u8.ToArray();
            await File.WriteAllBytesAsync(tempPdfPath, pdfBytes);
            string hash = Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant();
            BinaryData cacheJson = ModelReaderWriter.Write(fakeResult, ModelReaderWriterOptions.Json);
            await File.WriteAllBytesAsync(Path.Combine(tempDir, hash + "-formulas.json"), cacheJson.ToArray());

            var client = new DocumentIntelligenceClient(
                new Uri("https://example.invalid"), new AzureKeyCredential("unused-in-this-test"));
            var extractor = new AzureLayoutTextExtractor(new AzureDocumentAnalyzer(client, tempDir));

            IReadOnlyList<PageText> pages = await extractor.ExtractAsync(tempPdfPath);
            Assert.Equal(2, pages.Count);

            // Probe verdicts as DocnetTextLayerProbe would return them: page 1 has a text
            // layer, page 2 is image-only.
            var textLayer = new Dictionary<int, bool> { [1] = true, [2] = false };
            var classified = pages
                .OrderBy(p => p.PageNumber)
                .Select(p => (p.PageNumber,
                    Kind: PageOcrClassifier.Classify(textLayer[p.PageNumber], p.MeanConfidence)))
                .ToList();

            Assert.Equal(ChunkKind.Prose, classified[0].Kind);
            Assert.Equal(ChunkKind.OcrProse, classified[1].Kind);
            Assert.Equal(1, classified.Count(c => c.Kind == ChunkKind.OcrProse));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Classify_TextLayerPresent_HighConfidence_IsProse()
        => Assert.Equal(ChunkKind.Prose, PageOcrClassifier.Classify(hasTextLayer: true, 0.99f));

    [Fact]
    public void Classify_NoTextLayer_IsOcrProse_RegardlessOfConfidence()
        => Assert.Equal(ChunkKind.OcrProse, PageOcrClassifier.Classify(hasTextLayer: false, 0.999f));

    [Fact]
    public void Classify_TextLayerPresent_ButLowConfidence_IsOcrProse_SecondarySignal()
        => Assert.Equal(ChunkKind.OcrProse, PageOcrClassifier.Classify(hasTextLayer: true, 0.62f));
}
