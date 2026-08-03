using Athena.Core.Records;

namespace Athena.Ingestion.DocVectors;

/// <summary>
/// Document vector = L2-normalised mean of the document's chunk embeddings. Free (no extra
/// LLM/embedding calls) and faithful to actual content — which is exactly its weakness on long
/// multi-topic documents: B3's ~30 pages span retrieval, generation, augmentation and
/// evaluation, and their mean is a vector near the middle of the whole corpus, close to
/// everything and characteristic of nothing. For A5 (3 homogeneous pages) the same mean is an
/// excellent descriptor. That asymmetry is the §6.4 exercise, and it is why the recommender
/// symptom "same three documents for every seed" points here first (brief §16).
/// </summary>
public sealed class CentroidStrategy : IDocumentVectorStrategy
{
    public string Name => "centroid";

    public Task<ReadOnlyMemory<float>> BuildAsync(
        DocRecord doc, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
        => Task.FromResult(NormalisedMean(chunks.Select(c => c.Embedding).ToList()));

    /// <summary>Pure centroid arithmetic, exposed for unit tests: mean per dimension, then L2-normalise.</summary>
    internal static ReadOnlyMemory<float> NormalisedMean(IReadOnlyList<ReadOnlyMemory<float>> vectors)
    {
        if (vectors.Count == 0)
        {
            return ReadOnlyMemory<float>.Empty;
        }

        int dimensions = vectors[0].Length;
        var mean = new float[dimensions];
        foreach (ReadOnlyMemory<float> vector in vectors)
        {
            ReadOnlySpan<float> span = vector.Span;
            for (int i = 0; i < dimensions; i++)
            {
                mean[i] += span[i];
            }
        }

        for (int i = 0; i < dimensions; i++)
        {
            mean[i] /= vectors.Count;
        }

        // Normalise so cosine comparisons against unit-length query/chunk embeddings are
        // dot products and centroids of differently-sized documents are comparable.
        double norm = 0;
        for (int i = 0; i < dimensions; i++)
        {
            norm += (double)mean[i] * mean[i];
        }

        if (norm > 0)
        {
            float inv = (float)(1.0 / Math.Sqrt(norm));
            for (int i = 0; i < dimensions; i++)
            {
                mean[i] *= inv;
            }
        }

        return mean;
    }
}
