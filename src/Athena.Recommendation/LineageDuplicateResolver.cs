using Athena.Core;
using Athena.Core.Records;

namespace Athena.Recommendation;

/// <summary>Tunables for near-duplicate resolution, exposed so ablation 4 can sweep them.</summary>
public sealed record DuplicateResolutionOptions
{
    /// <summary>
    /// Ceiling for the metadata-less fallback. Same value and same defence as
    /// LineageOptions.MinCosine at ingestion: the engineered near-duplicates sit at 0.97–0.99
    /// doc-vector cosine while genuinely distinct same-cluster documents land visibly lower,
    /// so 0.95 splits the populations with margin. Using one number in both places keeps the
    /// two mechanisms telling one story.
    /// </summary>
    public double SimilarityCeiling { get; init; } = 0.95;

    public static DuplicateResolutionOptions Default { get; } = new();
}

/// <summary>
/// Near-duplicate resolution (brief §9.2), two rules in priority order — never a hardcoded
/// id list (that is a hand-edit dressed as a method, and a heavy deduction).
///
/// PRIMARY — computed lineage: within a <see cref="DocRecord.LineageGroup"/> (derived at
/// ingestion from doc-vector cosine + title similarity + cluster + dates), only the NEWEST
/// member surfaces; the rest become its <see cref="ResolvedDoc.SuppressedSiblings"/>.
/// Cost: accurate but the least general — it only works where ingestion's detector fired,
/// and it trusts <see cref="DocRecord.PublishedOn"/>. A fourth pair added to the corpus
/// tomorrow is detected at ingest with zero change here.
///
/// FALLBACK — similarity ceiling, only for documents in NO lineage group: a candidate whose
/// doc-vector cosine to any already-kept document (or the seed) exceeds the ceiling is
/// dropped. Cost: generalises to corpora without lineage metadata, but will eventually
/// suppress two genuinely distinct documents that happen to be written similarly — which is
/// why it is the fallback and not the primary rule.
///
/// Pure function: no I/O, no clock, no ambient state.
/// </summary>
public sealed class LineageDuplicateResolver : INearDuplicateResolver
{
    private readonly DuplicateResolutionOptions _options;

    public LineageDuplicateResolver(DuplicateResolutionOptions? options = null)
    {
        _options = options ?? DuplicateResolutionOptions.Default;
    }

    public IReadOnlyList<DocRecord> Resolve(IReadOnlyList<DocRecord> ranked, DocRecord? seed = null)
        => ResolveWithProvenance(ranked, seed).Select(r => r.Doc).ToList();

    public IReadOnlyList<ResolvedDoc> ResolveWithProvenance(
        IReadOnlyList<DocRecord> ranked, DocRecord? seed = null)
    {
        // Survivor per lineage group: newest PublishedOn wins regardless of rank (a reader
        // wants the final, not the higher-scoring draft); ties fall back to DocId ordinal.
        var groupSurvivors = ranked
            .Where(d => d.LineageGroup is not null)
            .GroupBy(d => d.LineageGroup!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.PublishedOn)
                      .ThenBy(d => d.DocId, StringComparer.Ordinal)
                      .First(),
                StringComparer.Ordinal);

        var result = new List<ResolvedDoc>();
        var emittedGroups = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<DocRecord>();

        foreach (DocRecord doc in ranked)
        {
            if (seed is not null && string.Equals(doc.DocId, seed.DocId, StringComparison.Ordinal))
            {
                continue; // never recommend the seed to itself
            }

            if (doc.LineageGroup is { } group)
            {
                // Seed guard: the sibling of the document the user is holding is exactly the
                // near-duplicate trap — its survivor is the seed itself, so nothing surfaces.
                if (seed?.LineageGroup is not null
                    && string.Equals(group, seed.LineageGroup, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!emittedGroups.Add(group))
                {
                    continue; // group already represented at its first-ranked position
                }

                DocRecord survivor = groupSurvivors[group];
                var suppressed = ranked
                    .Where(d => string.Equals(d.LineageGroup, group, StringComparison.Ordinal)
                                && !ReferenceEquals(d, survivor))
                    .ToList();
                result.Add(new ResolvedDoc(survivor, suppressed));
                kept.Add(survivor);
                continue;
            }

            // Ceiling fallback for ungrouped docs only — grouped docs are owned by the
            // lineage rule above, so the ceiling cannot double-suppress a group's survivor.
            bool nearDuplicateOfKept =
                (seed is not null && VectorMath.Cosine(doc.Embedding, seed.Embedding) > _options.SimilarityCeiling)
                || kept.Any(k => VectorMath.Cosine(doc.Embedding, k.Embedding) > _options.SimilarityCeiling);
            if (nearDuplicateOfKept)
            {
                continue;
            }

            result.Add(new ResolvedDoc(doc, []));
            kept.Add(doc);
        }

        return result;
    }
}
