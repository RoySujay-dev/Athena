using Athena.Core.Records;
using Microsoft.Extensions.AI;

namespace Athena.Ingestion.DocVectors;

/// <summary>
/// Document vector = embedding of "Title + Topics + Summary". Length-invariant: B3's thirty
/// pages and A5's three both collapse to a ~200-word description, so the long document stops
/// being a centroid blob (the CentroidStrategy failure mode). Topics act as discrete facets
/// that resist averaging; the title carries exact identifiers ("d516", "RAGAS") that also help
/// lexical matching downstream. Cost: the vector now depends on summariser quality — LLM
/// summary variance propagates straight into recommendation geometry, and content the summary
/// skipped simply does not exist for this vector. Compared against Centroid in ablation 2.
/// </summary>
public sealed class CompositeStrategy : IDocumentVectorStrategy
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

    public CompositeStrategy(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    {
        _embeddingGenerator = embeddingGenerator;
    }

    public string Name => "composite";

    public async Task<ReadOnlyMemory<float>> BuildAsync(
        DocRecord doc, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        Embedding<float> embedding = await _embeddingGenerator.GenerateAsync(
            BuildCompositeText(doc), cancellationToken: ct);
        return embedding.Vector;
    }

    /// <summary>Pure text assembly, exposed for unit tests.</summary>
    internal static string BuildCompositeText(DocRecord doc)
        => $"""
            Title: {doc.Title}
            Topics: {string.Join(", ", doc.Topics)}
            Summary: {doc.Summary}
            """;
}
