namespace Athena.Eval;

/// <summary>
/// One recommender gold case (brief §11.2): for a seed document, the hand-labelled set of
/// corpus documents that are genuinely useful follow-ups.
/// </summary>
public sealed record RecCase(string SeedDocId, IReadOnlyList<string> RelevantDocIds);
