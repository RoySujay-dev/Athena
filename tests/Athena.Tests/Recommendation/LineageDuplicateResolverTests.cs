using Athena.Core.Records;
using Athena.Recommendation;

namespace Athena.Tests.Recommendation;

/// <summary>
/// Fabricated DocRecords mirroring the real corpus shape: lineage pairs computed at ingestion
/// (A1 final Mar-2021 / A2 draft Aug-2020), plus ungrouped docs for the ceiling fallback.
/// </summary>
public sealed class LineageDuplicateResolverTests
{
    private static DocRecord Doc(
        string id, string? group = null, string published = "2021-01-01", double degrees = 0)
    {
        double radians = degrees * Math.PI / 180.0;
        return new DocRecord
        {
            DocId = id,
            Title = $"Title of {id}",
            LineageGroup = group,
            PublishedOn = DateTimeOffset.Parse(published),
            Embedding = new[] { (float)Math.Cos(radians), (float)Math.Sin(radians) },
        };
    }

    private readonly LineageDuplicateResolver _resolver = new();

    [Fact]
    public void LineageGroup_NewestSurvives_AtTheGroupsFirstRankedPosition()
    {
        // The draft outranks the final — the reader still gets the final, at the draft's slot.
        var a2Draft = Doc("A2", group: "A1", published: "2020-08-06", degrees: 1);
        var a1Final = Doc("A1", group: "A1", published: "2021-03-31", degrees: 0);
        var b1 = Doc("B1", degrees: 60);

        var resolved = _resolver.ResolveWithProvenance([a2Draft, b1, a1Final]);

        Assert.Equal(["A1", "B1"], resolved.Select(r => r.Doc.DocId));
        DocRecord suppressed = Assert.Single(resolved[0].SuppressedSiblings);
        Assert.Equal("A2", suppressed.DocId); // provenance feeds "supersedes d509, Aug 2020"
    }

    [Fact]
    public void PublicationDateTie_FallsBackToDocIdOrdinal()
    {
        var resolved = _resolver.Resolve(
        [
            Doc("C5", group: "C4", published: "2023-01-01", degrees: 1),
            Doc("C4", group: "C4", published: "2023-01-01", degrees: 0),
        ]);

        Assert.Equal("C4", Assert.Single(resolved).DocId);
    }

    [Fact]
    public void SeedsLineageSibling_NeverSurfaces()
    {
        // Recommending the draft of the document the user is holding is the trap (§9.2).
        var seed = Doc("A1", group: "A1", published: "2021-03-31");
        var resolved = _resolver.Resolve(
        [
            Doc("A2", group: "A1", published: "2020-08-06", degrees: 1),
            Doc("B1", degrees: 60),
        ], seed);

        Assert.Equal("B1", Assert.Single(resolved).DocId);
    }

    [Fact]
    public void SeedItself_IsDroppedFromResults()
    {
        var seed = Doc("B1", degrees: 60);

        var resolved = _resolver.Resolve([Doc("B1", degrees: 60), Doc("B2", degrees: 20)], seed);

        Assert.Equal("B2", Assert.Single(resolved).DocId);
    }

    [Fact]
    public void CeilingFallback_DropsUngroupedNearDuplicate_KeepsDistinctDocs()
    {
        // 2° apart → cosine ≈ 0.9994 > 0.95 ceiling: the lower-ranked twin is dropped.
        // 40° apart → cosine ≈ 0.766 < ceiling: genuinely distinct, both kept.
        var resolved = _resolver.Resolve(
        [
            Doc("X1", degrees: 0),
            Doc("X2", degrees: 2),
            Doc("Y1", degrees: 40),
        ]);

        Assert.Equal(["X1", "Y1"], resolved.Select(d => d.DocId));
    }

    [Fact]
    public void CeilingIsConfigurable_ForTheAblationSweep()
    {
        var strict = new LineageDuplicateResolver(new DuplicateResolutionOptions { SimilarityCeiling = 0.5 });

        // 40° apart (cos ≈ 0.766) survives the default 0.95 ceiling but not a 0.5 one.
        var resolved = strict.Resolve([Doc("X1", degrees: 0), Doc("Y1", degrees: 40)]);

        Assert.Equal("X1", Assert.Single(resolved).DocId);
    }

    [Fact]
    public void CeilingAlsoAppliesAgainstTheSeed()
    {
        var seed = Doc("SEED", degrees: 0);

        var resolved = _resolver.Resolve([Doc("X1", degrees: 2), Doc("Y1", degrees: 40)], seed);

        Assert.Equal("Y1", Assert.Single(resolved).DocId); // X1 is a near-copy of the seed
    }

    [Fact]
    public void GroupedDocs_AreOwnedByTheLineageRule_NotTheCeiling()
    {
        // A grouped survivor sits 2° from an already-kept ungrouped doc; the ceiling must not
        // double-suppress it — lineage decided it survives.
        var resolved = _resolver.Resolve(
        [
            Doc("B1", degrees: 0),
            Doc("A1", group: "A1", published: "2021-03-31", degrees: 2),
            Doc("A2", group: "A1", published: "2020-08-06", degrees: 3),
        ]);

        Assert.Equal(["B1", "A1"], resolved.Select(d => d.DocId));
    }

    [Fact]
    public void NoDuplicates_InputPassesThroughUnchanged()
    {
        var resolved = _resolver.ResolveWithProvenance(
            [Doc("A5", degrees: 0), Doc("B1", degrees: 50), Doc("C1", degrees: 100)]);

        Assert.Equal(["A5", "B1", "C1"], resolved.Select(r => r.Doc.DocId));
        Assert.All(resolved, r => Assert.Empty(r.SuppressedSiblings));
    }

    [Fact]
    public void Resolve_AndResolveWithProvenance_AgreeOnSurvivors()
    {
        var ranked = new[]
        {
            Doc("A2", group: "A1", published: "2020-08-06", degrees: 1),
            Doc("A1", group: "A1", published: "2021-03-31", degrees: 0),
            Doc("B1", degrees: 60),
        };

        Assert.Equal(
            _resolver.ResolveWithProvenance(ranked).Select(r => r.Doc.DocId),
            _resolver.Resolve(ranked).Select(d => d.DocId));
    }
}
