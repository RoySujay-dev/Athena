namespace Athena.Ingestion.Lineage;

/// <summary>What the detector needs to know about one document. Vectors must be L2-normalised.</summary>
public readonly record struct DocLineageSignals(
    string DocId,
    string Title,
    string Cluster,
    DateTimeOffset PublishedOn,
    ReadOnlyMemory<float> Vector);

/// <summary>Thresholds for lineage linking. Defaults are defended inline and in the README.</summary>
public sealed record LineageOptions
{
    /// <summary>
    /// The engineered draft/final and v1/v2 pairs sit at ~0.97–0.99 doc-vector cosine, while
    /// same-cluster-but-distinct documents (A1 vs A3: resilience vs operational-risk
    /// principles, same publisher, same house style) land visibly lower. 0.95 splits those
    /// populations with margin on both sides.
    /// </summary>
    public double MinCosine { get; init; } = 0.95;

    /// <summary>
    /// Token-set Jaccard over lowercased titles AFTER version-marker normalisation (see
    /// <see cref="LineageDetector.TitleJaccard"/>). Measured on the real corpus titles:
    /// engineered pairs land at ~1.0, the summary-of-a-sibling shape (A5 vs A1) at ~0.5, and
    /// distinct same-publisher documents (A1 vs A3) at ~0.36. 0.55 splits pair from non-pair
    /// with margin on both sides.
    /// </summary>
    public double MinTitleJaccard { get; init; } = 0.55;

    /// <summary>
    /// Versions of one document appear within a bounded window: the BCBS consult→final cycle
    /// is ~8 months, and the corpus's widest engineered pair (RAGAS v1→v2, measured from arXiv
    /// submission history) is ~19 months. 730 days covers both with headroom while still
    /// refusing to pair, say, a 2020 foundational paper with a 2024 survey that happens to
    /// read alike. A pair with unreliable dates would need the cosine-ceiling fallback instead.
    /// </summary>
    public TimeSpan MaxPublicationGap { get; init; } = TimeSpan.FromDays(730);

    public static LineageOptions Default { get; } = new();
}

/// <summary>
/// Computes version-lineage groups at ingestion time from signals — never from hand-authored
/// id lists (design constraint 2). Two documents are lineage-linked iff ALL of:
/// doc-vector cosine, title similarity, same cluster, and publication dates within a window.
/// Links are closed transitively (union-find), so a chain draft→final→scan groups together.
/// A fourth lineage pair added to the corpus tomorrow is detected here with no code change.
/// Pure: no I/O, no clock, no ambient state.
/// </summary>
public static class LineageDetector
{
    /// <summary>
    /// Returns DocId → lineage group id for every document that has at least one sibling.
    /// Documents with no siblings are absent (their LineageGroup stays null). The group id is
    /// the lexicographically smallest member DocId — stable across re-runs, derived not typed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssignGroups(
        IReadOnlyList<DocLineageSignals> docs, LineageOptions? options = null)
    {
        options ??= LineageOptions.Default;

        int[] parent = Enumerable.Range(0, docs.Count).ToArray();

        for (int i = 0; i < docs.Count; i++)
        {
            for (int j = i + 1; j < docs.Count; j++)
            {
                if (AreLinked(docs[i], docs[j], options))
                {
                    Union(parent, i, j);
                }
            }
        }

        // Group id = smallest DocId in the component; singletons stay ungrouped.
        var groupIds = new Dictionary<int, string>();
        var componentSizes = new Dictionary<int, int>();
        for (int i = 0; i < docs.Count; i++)
        {
            int root = Find(parent, i);
            componentSizes[root] = componentSizes.GetValueOrDefault(root) + 1;
            if (!groupIds.TryGetValue(root, out string? smallest)
                || string.CompareOrdinal(docs[i].DocId, smallest) < 0)
            {
                groupIds[root] = docs[i].DocId;
            }
        }

        var result = new Dictionary<string, string>();
        for (int i = 0; i < docs.Count; i++)
        {
            int root = Find(parent, i);
            if (componentSizes[root] > 1)
            {
                result[docs[i].DocId] = groupIds[root];
            }
        }

        return result;
    }

    private static bool AreLinked(in DocLineageSignals a, in DocLineageSignals b, LineageOptions options)
        => string.Equals(a.Cluster, b.Cluster, StringComparison.Ordinal)
           && (a.PublishedOn - b.PublishedOn).Duration() <= options.MaxPublicationGap
           && TitleJaccard(a.Title, b.Title) >= options.MinTitleJaccard
           && Cosine(a.Vector, b.Vector) > options.MinCosine;

    /// <summary>
    /// Token-set Jaccard over lowercased, punctuation-stripped titles with VERSION MARKERS
    /// removed first. Version markers — status words ("final", "consultative draft"), dates,
    /// version tags ("v1"), and short publisher document codes ("d516") — are precisely what
    /// differs between editions of the same work, so raw Jaccard scores a draft/final pair
    /// LOWER (0.36 on A1/A2) than two distinct documents that share a date and status (0.41 on
    /// A1/A3). Stripping them compares what the work is about, not which edition it is: pairs
    /// go to ~1.0, A1/A3 drops to ~0.36. The rule is generic — a fourth pair added tomorrow
    /// needs no new words here.
    /// </summary>
    internal static double TitleJaccard(string titleA, string titleB)
    {
        HashSet<string> tokensA = Tokenise(titleA);
        HashSet<string> tokensB = Tokenise(titleB);
        if (tokensA.Count == 0 || tokensB.Count == 0)
        {
            return 0;
        }

        int intersection = tokensA.Intersect(tokensB).Count();
        int union = tokensA.Count + tokensB.Count - intersection;
        return (double)intersection / union;
    }

    private static readonly HashSet<string> StatusWords = new(StringComparer.Ordinal)
    {
        "draft", "final", "consultative", "revised", "revision", "revisions",
        "version", "edition", "updated",
    };

    private static readonly HashSet<string> MonthNames = new(StringComparer.Ordinal)
    {
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
        "january", "february", "march", "april", "june", "july", "august", "september",
        "october", "november", "december",
    };

    private static HashSet<string> Tokenise(string title)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = new(raw.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (token.Length > 0 && !IsVersionMarker(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static bool IsVersionMarker(string token)
        => StatusWords.Contains(token)
           || MonthNames.Contains(token)
           // Years and other bare numbers ("2021").
           || token.All(char.IsDigit)
           // Version tags and publisher document codes: a 1-3 letter prefix plus digits
           // ("v1", "d516", "rev3"). Generic shape, not a corpus-specific id list.
           || (token.Length >= 2 && token.Length <= 6
               && char.IsLetter(token[0])
               && token.TakeWhile(char.IsLetter).Count() <= 3
               && token.SkipWhile(char.IsLetter).All(char.IsDigit)
               && token.Any(char.IsDigit));

    // Kept as an internal alias for existing callers; the implementation moved to Core so
    // the recommender's pure functions (MMR, dedup) share the same cosine.
    internal static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
        => Athena.Core.VectorMath.Cosine(a, b);

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    private static void Union(int[] parent, int i, int j)
    {
        int rootI = Find(parent, i);
        int rootJ = Find(parent, j);
        if (rootI != rootJ)
        {
            parent[rootJ] = rootI;
        }
    }
}
