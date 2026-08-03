using Athena.Core.Records;

namespace Athena.Retrieval;

/// <summary>Outcome of resolving a user-supplied document identifier against the catalog.</summary>
public sealed record DocIdResolution(DocRecord? Match, IReadOnlyList<DocRecord> Ambiguous)
{
    public static DocIdResolution Of(DocRecord match) => new(match, []);

    public static readonly DocIdResolution None = new(null, []);
}

/// <summary>
/// Resolves the identifier a USER uses for a document ("d516", "the RAPTOR one", "A1") to the
/// catalog's DocId. Users name documents by publication number or title fragment far more
/// often than by our internal id, and a docId filter built from an unresolved identifier
/// silently matches zero chunks — observed live: hybrid_search(docId: "d516") retrieved
/// nothing and the answer claimed d516 says nothing about its own subject. This is
/// argument→catalog entity resolution inside a function the model already chose — not intent
/// routing, which stays entirely with FunctionChoiceBehavior.Auto(). Pure — unit-tested.
/// </summary>
public static class DocIdResolver
{
    public static DocIdResolution Resolve(IReadOnlyList<DocRecord> docs, string identifier)
    {
        identifier = identifier.Trim();
        if (identifier.Length == 0)
        {
            return DocIdResolution.None;
        }

        DocRecord? exact = docs.FirstOrDefault(d =>
            string.Equals(d.DocId, identifier, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.Title, identifier, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return DocIdResolution.Of(exact);
        }

        // Substring on title catches publication numbers ("d516"), author shorthand
        // ("RAPTOR"), and partial titles. Unique hit resolves; several hits stay ambiguous —
        // guessing between a draft and its final would be exactly the wrong place to guess.
        var matches = docs
            .Where(d => d.Title.Contains(identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? DocIdResolution.Of(matches[0]) : new DocIdResolution(null, matches);
    }
}
