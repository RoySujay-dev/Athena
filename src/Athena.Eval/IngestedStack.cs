using Athena.Core.Records;
using Athena.Ingestion.Embeddings;
using Athena.Retrieval;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;

namespace Athena.Eval;

/// <summary>
/// One fully-ingested system: collections populated, Lucene index built, retrievers wired.
/// Owns the disposables it wraps.
/// </summary>
public sealed record IngestedStack(
    Kernel Kernel,
    CachingEmbeddingGenerator EmbeddingGenerator,
    VectorStoreCollection<string, ChunkRecord> Chunks,
    VectorStoreCollection<string, DocRecord> Docs,
    DenseRetriever Dense,
    LuceneLexicalRetriever Lexical) : IDisposable
{
    public void Dispose()
    {
        Lexical.Dispose();
        EmbeddingGenerator.Dispose();
    }
}
