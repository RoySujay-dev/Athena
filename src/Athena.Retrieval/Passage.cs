namespace Athena.Retrieval;

/// <summary>
/// One retrieved unit of evidence, per the brief's §7 skeleton. <see cref="PageNumber"/> is
/// carried from <c>ChunkRecord</c> so downstream citations can always name a page (hard
/// constraint 7). <see cref="Score"/> is stage-relative: cosine similarity from the dense
/// retriever, BM25 from the lexical one, an RRF sum after fusion, and a 0–10 relevance grade
/// after reranking — comparable within one list, never across stages.
/// <see cref="EndPage"/> (addition to the brief's skeleton, README-documented) carries the
/// chunk's page span for citation validation; 0 means "unknown", treated as PageNumber.
/// </summary>
public readonly record struct Passage(
    string ChunkId, string DocId, string Title, int PageNumber, string Text, double Score,
    int EndPage = 0);
