using Athena.Core.Records;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// Pure per-page classifier: text-layer-derived (<see cref="ChunkKind.Prose"/>) vs
/// image-OCR-derived (<see cref="ChunkKind.OcrProse"/>). No I/O, no Azure types.
///
/// PRIMARY signal — text-layer presence (from <see cref="ITextLayerProbe"/>): the brief's
/// §6.2 rule ("under ~50 chars from the native extractor → OCR"), translated. A page with no
/// native text layer is image-OCR-derived by construction, whatever confidence DI reports.
///
/// SECONDARY signal — DI mean word confidence: retained for the case the probe cannot see, a
/// text-layer page whose DI read nonetheless carried real recognition uncertainty (stamps,
/// rotated annexes, degraded embedded scans WITH an OCR'd text layer already present).
///
/// The confidence threshold alone was the original IDP adaptation and it FAILED empirically:
/// on the manufactured 200-DPI A1-SCAN, DI reported mean word confidence 0.9913 vs 0.9902 on
/// native A1 — identical distributions, no threshold separates them. That negative result is
/// reported in REPORT.md; the probe is what restores the brief's per-page classification.
/// </summary>
public static class PageOcrClassifier
{
    /// <summary>
    /// 0.90 sits below the native-text noise floor (~0.97+ per-page means measured corpus-wide)
    /// and above where meaningful OCR uncertainty begins, so an occasional low-confidence word
    /// on an otherwise-native page does not misclassify the whole page.
    /// </summary>
    public const float DefaultConfidenceThreshold = 0.90f;

    public static ChunkKind Classify(bool hasTextLayer, float meanConfidence,
                                     float threshold = DefaultConfidenceThreshold)
        => !hasTextLayer || meanConfidence < threshold ? ChunkKind.OcrProse : ChunkKind.Prose;
}
