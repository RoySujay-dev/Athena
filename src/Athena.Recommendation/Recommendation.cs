namespace Athena.Recommendation;

/// <summary>
/// Per-signal breakdown behind one recommendation's score. Carried on the result (DEVIATION
/// from the brief's §9.1 skeleton, README-documented) because §9.5 demands reasons generated
/// from the signals that actually produced the ranking — the presentation layer reads this
/// instead of inventing decoration, and the eval harness logs it.
/// </summary>
/// <param name="DocSim">Cosine of query vector vs document vector.</param>
/// <param name="ChunkAggregate">Rank-weighted, capped, length-normalised chunk-hit signal, max-normalised to [0,1] across the candidate set.</param>
/// <param name="Recency">Floored exponential age decay, in [floor, 1].</param>
/// <param name="ChunkHits">Number of chunk hits counted (after the per-doc cap).</param>
/// <param name="BestHitRank">1-based rank of the doc's best chunk hit, 0 if none.</param>
public readonly record struct SignalBreakdown(
    double DocSim, double ChunkAggregate, double Recency, int ChunkHits, int BestHitRank);

/// <summary>
/// One recommended document (brief §9.1) plus its <see cref="Signals"/> breakdown.
/// <see cref="Reason"/> is left empty by the scorer — composing the one-line reason belongs
/// to the presentation layer, which combines these signals with dedup provenance and profile
/// context the scorer cannot see.
/// </summary>
public readonly record struct Recommendation(
    string DocId, string Title, string Reason, IReadOnlyList<string> Topics, double Score,
    SignalBreakdown Signals);
