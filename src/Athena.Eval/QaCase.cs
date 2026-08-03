namespace Athena.Eval;

/// <summary>
/// One QA gold case (brief §11). <see cref="GoldPages"/> are 1-based physical page numbers of
/// <see cref="GoldDocId"/> — the same numbering ChunkRecord.PageNumber carries from extraction.
/// Unanswerable cases (<see cref="IsAnswerable"/> false) exist for the abstention metric; the
/// retrieval metrics skip them.
/// </summary>
public sealed record QaCase(string Question, string ExpectedAnswer, string GoldDocId,
                            int[] GoldPages, bool IsAnswerable);
