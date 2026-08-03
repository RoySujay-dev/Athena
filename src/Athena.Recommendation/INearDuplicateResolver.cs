using Athena.Core.Records;

namespace Athena.Recommendation;

/// <summary>Collapses near-identical documents to one representative (brief §9.2).</summary>
public interface INearDuplicateResolver
{
    /// <summary>Collapse near-identical documents to one representative before the list is returned.</summary>
    IReadOnlyList<DocRecord> Resolve(IReadOnlyList<DocRecord> ranked, DocRecord? seed = null);

    /// <summary>
    /// Same collapse, keeping what each survivor suppressed — the reason generator's
    /// "supersedes d509, Aug 2020" clause reads this (see <see cref="ResolvedDoc"/>).
    /// </summary>
    IReadOnlyList<ResolvedDoc> ResolveWithProvenance(IReadOnlyList<DocRecord> ranked, DocRecord? seed = null);
}

/// <summary>
/// One surviving document plus the lineage siblings it suppressed. DEVIATION from the brief's
/// bare-list skeleton (documented in README): §9.5 requires the survivor's reason to name what
/// it superseded ("supersedes d509, Aug 2020"), so the resolver must not throw that
/// information away — the presentation layer builds the reason string from this.
/// </summary>
public sealed record ResolvedDoc(DocRecord Doc, IReadOnlyList<DocRecord> SuppressedSiblings);
