using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Athena.Ingestion.Embeddings;

/// <summary>
/// Disk-cache decorator over an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, keyed by
/// SHA-256 of (model id + text). The DI cache alone does not keep re-index under the brief's
/// two minutes — a full run makes thousands of embedding calls, and this is the cache that
/// removes them. Lives in <c>corpus/.embedding-cache/</c> (gitignored), one small JSON file
/// per distinct text: crude but transparent, debuggable, and safely incremental.
/// </summary>
public sealed class CachingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly string _cacheDirectory;
    private readonly string _modelId;

    public CachingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner, string cacheDirectory, string modelId)
    {
        _inner = inner;
        _cacheDirectory = cacheDirectory;
        _modelId = modelId;
        Directory.CreateDirectory(cacheDirectory);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var results = new Embedding<float>?[texts.Count];
        var missing = new List<int>();

        for (int i = 0; i < texts.Count; i++)
        {
            float[]? cached = TryReadCache(CachePath(texts[i]));
            if (cached is not null)
            {
                results[i] = new Embedding<float>(cached);
            }
            else
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            GeneratedEmbeddings<Embedding<float>> generated = await _inner.GenerateAsync(
                missing.Select(i => texts[i]), options, cancellationToken);
            if (generated.Count != missing.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding generator returned {generated.Count} embeddings for {missing.Count} inputs.");
            }

            for (int m = 0; m < missing.Count; m++)
            {
                results[missing[m]] = generated[m];
                await File.WriteAllTextAsync(
                    CachePath(texts[missing[m]]),
                    JsonSerializer.Serialize(generated[m].Vector.ToArray()),
                    cancellationToken);
            }
        }

        return new GeneratedEmbeddings<Embedding<float>>(results.Select(r => r!));
    }

    private string CachePath(string text)
    {
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(_modelId + "\n" + text))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"{hash}.json");
    }

    private static float[]? TryReadCache(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<float[]>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // A truncated cache file (killed run) is re-generated, not fatal.
            return null;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
