using System.Text.Json;

namespace Athena.Ingestion;

/// <summary>One entry of <c>corpus/manifest.json</c>. Mirrors the committed JSON exactly.</summary>
/// <remarks>
/// <c>PublishedOn</c> is the publication date of the revision the URL actually serves (verified
/// against BIS / arXiv submission histories), not the date of v1 — versionless arXiv URLs serve
/// the latest revision. Lineage detection and recency scoring both depend on this field.
/// </remarks>
public sealed record ManifestEntry(
    string Id,
    string Title,
    string Url,
    string Cluster,
    int ExpectedPages,
    DateTimeOffset PublishedOn);

public sealed record CorpusManifest(IReadOnlyList<ManifestEntry> Documents)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<CorpusManifest> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<CorpusManifest>(stream, JsonOptions, ct);
        return manifest ?? throw new InvalidOperationException($"Manifest at '{path}' deserialised to null.");
    }
}
