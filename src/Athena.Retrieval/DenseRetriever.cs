using Athena.Core.Records;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Athena.Retrieval;

/// <summary>
/// Dense retrieval over the chunk collection: embed the query with the SAME embedding model
/// that embedded the chunks at ingestion, then cosine-search the vector store. The optional
/// docId restriction is pushed into the store as a pre-filter, so topK is always counted
/// within the restricted set, not filtered out of a global top-K afterwards.
/// </summary>
public sealed class DenseRetriever : IDenseRetriever
{
    private readonly VectorStoreCollection<string, ChunkRecord> _chunks;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

    public DenseRetriever(
        VectorStoreCollection<string, ChunkRecord> chunks,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    {
        _chunks = chunks;
        _embeddingGenerator = embeddingGenerator;
    }

    public async Task<IReadOnlyList<Passage>> SearchAsync(
        string query, int topK, string? docId = null, CancellationToken ct = default)
    {
        GeneratedEmbeddings<Embedding<float>> embeddings = await _embeddingGenerator.GenerateAsync(
            [query], cancellationToken: ct);
        ReadOnlyMemory<float> queryVector = embeddings[0].Vector;

        var options = new VectorSearchOptions<ChunkRecord>();
        if (docId is not null)
        {
            options.Filter = c => c.DocId == docId;
        }

        var passages = new List<Passage>(topK);
        await foreach (VectorSearchResult<ChunkRecord> result in
            _chunks.SearchAsync(queryVector, topK, options, ct))
        {
            ChunkRecord chunk = result.Record;
            passages.Add(new Passage(
                chunk.ChunkId, chunk.DocId, chunk.Title, chunk.PageNumber, chunk.Text,
                result.Score ?? 0d, chunk.EndPage));
        }

        return passages;
    }
}
