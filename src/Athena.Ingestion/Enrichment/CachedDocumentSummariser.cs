using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athena.Core.Records;

namespace Athena.Ingestion.Enrichment;

/// <summary>
/// Disk-cache decorator over any <see cref="IDocumentSummariser"/>, keyed by a hash of the
/// document text. Same rationale as the DI response cache (hard constraint 11): summaries are
/// paid LLM calls, and re-index must read caches, not networks, to stay under two minutes.
/// Cache lives in <c>corpus/.enrichment-cache/</c> (gitignored).
/// </summary>
public sealed class CachedDocumentSummariser : IDocumentSummariser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IDocumentSummariser _inner;
    private readonly string _cacheDirectory;

    public CachedDocumentSummariser(IDocumentSummariser inner, string cacheDirectory)
    {
        _inner = inner;
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
    }

    public async Task<DocumentSummary> SummariseAsync(
        DocumentMetadata meta, string documentText, CancellationToken ct = default)
    {
        // Content-hash key: a re-fetched PDF whose text changed re-summarises automatically;
        // an unchanged one never pays twice.
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(documentText))).ToLowerInvariant();
        string cachePath = Path.Combine(_cacheDirectory, $"{hash}.summary.json");

        if (File.Exists(cachePath))
        {
            await using FileStream read = File.OpenRead(cachePath);
            DocumentSummary? cached = await JsonSerializer.DeserializeAsync<DocumentSummary>(
                read, JsonOptions, ct);
            if (cached is not null)
            {
                return cached;
            }
        }

        DocumentSummary summary = await _inner.SummariseAsync(meta, documentText, ct);
        await File.WriteAllTextAsync(
            cachePath, JsonSerializer.Serialize(summary, JsonOptions), ct);
        return summary;
    }
}
