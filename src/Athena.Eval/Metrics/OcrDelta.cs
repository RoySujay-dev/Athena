using Athena.Retrieval;

namespace Athena.Eval.Metrics;

/// <summary>
/// §11.1: Context Recall on the manufactured scan minus the same on the text-native original
/// — OCR quality in one number, here DI's image-OCR path vs its text-layer path. Per
/// applicable case the value is recallScan − recallNative ∈ {−1, 0, +1}; the mean over
/// applicable cases is the delta. Applicable = the case's gold doc HAS a scanned twin and its
/// gold pages exist in the scanned page range. Both retrievals are docId-filtered so each
/// copy is judged on its own chunks only. The native/scan pairing is DISCOVERED from the
/// ingested corpus by the factory (the "{id}-SCAN" ingestion convention), never typed here.
/// </summary>
public sealed class OcrDelta : IMetric<QaCase>
{
    private readonly Func<string, string?, CancellationToken, Task<IReadOnlyList<Passage>>> _retrieve;
    private readonly string _nativeDocId;
    private readonly string _scanDocId;
    private readonly int _scannedPageCount;

    public OcrDelta(
        Func<string, string?, CancellationToken, Task<IReadOnlyList<Passage>>> retrieve,
        string nativeDocId, string scanDocId, int scannedPageCount)
    {
        _retrieve = retrieve;
        _nativeDocId = nativeDocId;
        _scanDocId = scanDocId;
        _scannedPageCount = scannedPageCount;
    }

    public string Name => "OcrDelta";

    public async Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
    {
        if (!testCase.IsAnswerable
            || !string.Equals(testCase.GoldDocId, _nativeDocId, StringComparison.Ordinal)
            || testCase.GoldPages.Any(p => p > _scannedPageCount))
        {
            return double.NaN;
        }

        IReadOnlyList<Passage> native = await _retrieve(testCase.Question, _nativeDocId, ct);
        IReadOnlyList<Passage> scanned = await _retrieve(testCase.Question, _scanDocId, ct);

        double nativeRecall = native.Any(p => testCase.GoldPages.Contains(p.PageNumber)) ? 1 : 0;
        double scanRecall = scanned.Any(p => testCase.GoldPages.Contains(p.PageNumber)) ? 1 : 0;
        return scanRecall - nativeRecall;
    }
}
