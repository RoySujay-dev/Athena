using Athena.Ingestion.Lineage;

namespace Athena.Tests.Lineage;

public sealed class LineageDetectorTests
{
    private static readonly DateTimeOffset Final = new(2021, 3, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Draft = new(2020, 8, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Unit vector at an angle in a 2D plane — cosine between two = cos(delta).</summary>
    private static ReadOnlyMemory<float> At(double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        return new[] { (float)Math.Cos(radians), (float)Math.Sin(radians) };
    }

    private static DocLineageSignals Doc(
        string id, string title, string cluster, DateTimeOffset published, double degrees)
        => new(id, title, cluster, published, At(degrees));

    [Fact]
    public void Links_a_draft_final_shaped_pair()
    {
        // ~5 degrees apart => cosine ~0.996, near-identical titles, same cluster, ~8 months.
        var docs = new List<DocLineageSignals>
        {
            Doc("X1", "Principles for Widget Resilience (final)", "A", Final, 0),
            Doc("X2", "Principles for Widget Resilience (consultative draft)", "A", Draft, 5),
        };

        var groups = LineageDetector.AssignGroups(docs);

        Assert.Equal("X1", groups["X1"]);
        Assert.Equal("X1", groups["X2"]);
    }

    [Fact]
    public void Does_not_link_similar_house_style_documents_with_different_titles()
    {
        // The A1-vs-A3 shape: same publisher/cluster/date, similar-ish vectors (~20deg =>
        // cosine ~0.94, below threshold) AND nearly disjoint titles. Either signal alone
        // must be enough to refuse the link.
        var docs = new List<DocLineageSignals>
        {
            Doc("X1", "Principles for Widget Resilience (final)", "A", Final, 0),
            Doc("X3", "Revisions to the Management of Gadget Risk (final)", "A", Final, 20),
        };

        Assert.Empty(LineageDetector.AssignGroups(docs));
    }

    [Fact]
    public void Does_not_link_near_identical_vectors_when_titles_differ()
    {
        // High cosine alone (2deg => ~0.999) must not create lineage: two genuinely distinct
        // documents can be similarly written.
        var docs = new List<DocLineageSignals>
        {
            Doc("X1", "Principles for Widget Resilience", "A", Final, 0),
            Doc("X3", "Annual Report on Sprocket Markets", "A", Final, 2),
        };

        Assert.Empty(LineageDetector.AssignGroups(docs));
    }

    [Fact]
    public void Does_not_link_across_clusters()
    {
        var docs = new List<DocLineageSignals>
        {
            Doc("B9", "A Survey of Widget Retrieval", "B", Final, 0),
            Doc("C9", "A Survey of Widget Retrieval", "C", Final, 1),
        };

        Assert.Empty(LineageDetector.AssignGroups(docs));
    }

    [Fact]
    public void Does_not_link_publications_years_apart()
    {
        var docs = new List<DocLineageSignals>
        {
            Doc("X1", "Widget Retrieval for Knowledge Tasks", "B", new DateTimeOffset(2020, 5, 22, 0, 0, 0, TimeSpan.Zero), 0),
            Doc("X2", "Widget Retrieval for Knowledge Tasks v2", "B", new DateTimeOffset(2024, 3, 27, 0, 0, 0, TimeSpan.Zero), 1),
        };

        Assert.Empty(LineageDetector.AssignGroups(docs));
    }

    [Fact]
    public void Links_arxiv_style_revisions_nineteen_months_apart()
    {
        // The measured C4/C5 gap (v1 2023-09-26 -> v2 2025-04-28, ~580 days) must fit the window.
        var docs = new List<DocLineageSignals>
        {
            Doc("C4", "RAGAS Automated Evaluation (v1)", "C", new DateTimeOffset(2023, 9, 26, 0, 0, 0, TimeSpan.Zero), 0),
            Doc("C5", "RAGAS Automated Evaluation (v2)", "C", new DateTimeOffset(2025, 4, 28, 0, 0, 0, TimeSpan.Zero), 3),
        };

        Assert.Equal(2, LineageDetector.AssignGroups(docs).Count);
    }

    [Fact]
    public void Chains_close_transitively_and_group_id_is_smallest_member()
    {
        // draft <-> final <-> scanned copy: the scan links to the final only, but all three
        // must share one group via union-find.
        var docs = new List<DocLineageSignals>
        {
            Doc("X2", "Principles for Widget Resilience (draft)", "A", Draft, 6),
            Doc("X1", "Principles for Widget Resilience (final)", "A", Final, 0),
            Doc("X1-SCAN", "Principles for Widget Resilience (final)", "A", Final, 1),
        };

        var groups = LineageDetector.AssignGroups(docs);

        Assert.Equal(3, groups.Count);
        Assert.All(groups.Values, g => Assert.Equal("X1", g));
    }

    [Fact]
    public void Corpus_shaped_input_finds_exactly_the_three_engineered_pairs()
    {
        // Synthetic stand-ins mirroring the real corpus geometry: three engineered pairs at
        // ~0.99 cosine plus distinct same-cluster neighbours at safe angular distance.
        var docs = new List<DocLineageSignals>
        {
            Doc("A1", "Principles for Operational Resilience (final, Mar 2021) BCBS d516", "A", Final, 0),
            Doc("A2", "Principles for Operational Resilience (consultative draft, Aug 2020) BCBS d509", "A", Draft, 5),
            Doc("A3", "Revisions to the Principles for the Sound Management of Operational Risk (final) BCBS d515", "A", Final, 30),
            Doc("A4", "Revisions to the Principles for the Sound Management of Operational Risk (draft) BCBS d508", "A", Draft, 34),
            Doc("A5", "FSI Executive Summary Principles for operational resilience", "A", new DateTimeOffset(2022, 9, 29, 0, 0, 0, TimeSpan.Zero), 15),
            Doc("C4", "RAGAS Automated Evaluation of Retrieval Augmented Generation (v1)", "C", new DateTimeOffset(2023, 9, 26, 0, 0, 0, TimeSpan.Zero), 60),
            Doc("C5", "RAGAS Automated Evaluation of Retrieval Augmented Generation (v2)", "C", new DateTimeOffset(2025, 4, 28, 0, 0, 0, TimeSpan.Zero), 63),
        };

        var groups = LineageDetector.AssignGroups(docs);

        Assert.Equal(6, groups.Count); // A5 is a singleton
        Assert.Equal("A1", groups["A1"]);
        Assert.Equal("A1", groups["A2"]);
        Assert.Equal("A3", groups["A3"]);
        Assert.Equal("A3", groups["A4"]);
        Assert.Equal("C4", groups["C4"]);
        Assert.Equal("C4", groups["C5"]);
        Assert.False(groups.ContainsKey("A5"));
        Assert.NotEqual(groups["A1"], groups["A3"]);
    }

    [Fact]
    public void Title_jaccard_behaves_on_the_real_title_shapes()
    {
        // Version-marker normalisation is what makes these behave: WITHOUT it the A1/A2 pair
        // scores 0.36 — lower than the A1/A3 non-pair at 0.41 — because editions differ in
        // exactly the tokens ("final", "Mar 2021", "d516") that non-pairs share.
        double gate = LineageOptions.Default.MinTitleJaccard;
        double pairSimilarity = LineageDetector.TitleJaccard(
            "Principles for Operational Resilience (final, Mar 2021) — BCBS d516",
            "Principles for Operational Resilience (consultative draft, Aug 2020) — BCBS d509");
        double nonPairSimilarity = LineageDetector.TitleJaccard(
            "Principles for Operational Resilience (final, Mar 2021) — BCBS d516",
            "Revisions to the Principles for the Sound Management of Operational Risk (final, Mar 2021) — BCBS d515");
        double summaryOfSiblingSimilarity = LineageDetector.TitleJaccard(
            "Principles for Operational Resilience (final, Mar 2021) — BCBS d516",
            "FSI Executive Summary: Principles for operational resilience");

        Assert.True(pairSimilarity >= gate, $"pair similarity {pairSimilarity} should pass the {gate} gate");
        Assert.True(nonPairSimilarity < gate, $"non-pair similarity {nonPairSimilarity} should fail the {gate} gate");
        Assert.True(summaryOfSiblingSimilarity < gate, $"summary-of-sibling similarity {summaryOfSiblingSimilarity} should fail the {gate} gate");
    }
}
