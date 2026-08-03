using Athena.Core.Records;

namespace Athena.Ingestion.DocVectors;

/// <summary>
/// Builds <see cref="DocRecord.Embedding"/> — "the decision that makes or breaks Part D"
/// (brief §6.4). At least two strategies are implemented and compared in the Part F ablation.
/// </summary>
public interface IDocumentVectorStrategy
{
    string Name { get; }

    Task<ReadOnlyMemory<float>> BuildAsync(
        DocRecord doc, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default);
}
