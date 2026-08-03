namespace Athena.Core.Records;

/// <summary>
/// Manifest-sourced facts about a document, handed to chunkers and the summariser. This is the
/// brief's §6.3 <c>DocumentMetadata</c>; it lives in Core so chunkers depend only on Core types.
/// </summary>
public sealed record DocumentMetadata(
    string DocId,
    string Title,
    string Cluster,
    DateTimeOffset PublishedOn,
    int PageCount);
