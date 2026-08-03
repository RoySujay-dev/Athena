using System.ClientModel.Primitives;
using System.Security.Cryptography;
using Azure;
using Azure.AI.DocumentIntelligence;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// The sole caller of Azure Document Intelligence. Calls <c>prebuilt-layout</c> at most once
/// per distinct document (by content hash) ever, caching the full raw <see cref="AnalyzeResult"/>
/// as JSON under <paramref name="cacheDirectory"/> (design rule 11: Azure DI is a paid,
/// per-page, network service, and re-indexing must read cache, not call DI again).
/// </summary>
public sealed class AzureDocumentAnalyzer : IDocumentAnalyzer
{
    private const string ModelId = "prebuilt-layout";

    private readonly DocumentIntelligenceClient _client;
    private readonly string _cacheDirectory;

    public AzureDocumentAnalyzer(DocumentIntelligenceClient client, string cacheDirectory)
    {
        _client = client;
        _cacheDirectory = cacheDirectory;

        // A first run must not fail on a missing cache folder.
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<AnalyzeResult> AnalyzeAsync(string pdfPath, CancellationToken ct = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(pdfPath, ct);

        // Keyed by CONTENT hash, not path: two identically-named files with different bytes
        // must land in different cache entries, and two different paths with identical bytes
        // should share one cache entry (rule 11: "keyed by file content hash").
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // The suffix versions the cache by FEATURE SET: "-formulas" entries were analyzed with
        // the formulas add-on. Plain "{hash}.json" entries from earlier runs are not deleted,
        // but must not be served for a request whose analysis options differ.
        string cachePath = Path.Combine(_cacheDirectory, hash + "-formulas.json");

        if (File.Exists(cachePath))
        {
            // Cache hit: never touch the client. This is the entire point of the cache --
            // repeated ingestion runs (e.g. re-chunking after a bug fix) must not re-pay for
            // or re-wait on a network call to a paid, per-page service.
            byte[] cached = await File.ReadAllBytesAsync(cachePath, ct);
            return ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromBytes(cached), ModelReaderWriterOptions.Json)!;
        }

        var options = new AnalyzeDocumentOptions(ModelId, BinaryData.FromBytes(bytes));
        // Formulas add-on: layout text alone linearizes typeset math into lookalike letters
        // (observed live: p_η/p_θ in B1 became P_n/P_o), which poisons every downstream answer
        // that quotes an equation. The add-on returns each equation's LaTeX, which the
        // extractors splice back into page text. Paid add-on (README "Deviations"), but still
        // one DI call per document, cached forever.
        options.Features.Add(DocumentAnalysisFeature.Formulas);
        Operation<AnalyzeResult> operation = await _client.AnalyzeDocumentAsync(WaitUntil.Completed, options, ct);
        AnalyzeResult result = operation.Value;

        BinaryData json = ModelReaderWriter.Write(result, ModelReaderWriterOptions.Json);
        await File.WriteAllBytesAsync(cachePath, json.ToArray(), ct);

        return result;
    }
}
