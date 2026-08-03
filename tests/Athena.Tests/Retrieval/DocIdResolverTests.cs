using Athena.Core.Records;
using Athena.Retrieval;

namespace Athena.Tests.Retrieval;

public sealed class DocIdResolverTests
{
    private static readonly IReadOnlyList<DocRecord> Docs =
    [
        new() { DocId = "A1", Title = "Principles for Operational Resilience (final, Mar 2021) — BCBS d516" },
        new() { DocId = "A2", Title = "Principles for Operational Resilience (consultative draft, Aug 2020) — BCBS d509" },
        new() { DocId = "B4", Title = "Sarthi et al., RAPTOR: Recursive Abstractive Processing for Tree-Organized Retrieval" },
    ];

    [Theory]
    [InlineData("A1", "A1")]     // exact library id
    [InlineData("a1", "A1")]     // case-insensitive
    [InlineData("d516", "A1")]   // publication number — the live failure
    [InlineData("RAPTOR", "B4")] // author shorthand
    public void Resolve_MapsUserIdentifierToDocId(string identifier, string expectedDocId)
        => Assert.Equal(expectedDocId, DocIdResolver.Resolve(Docs, identifier).Match?.DocId);

    [Fact]
    public void Resolve_AmbiguousFragment_ReturnsAllCandidates_NoGuess()
    {
        DocIdResolution resolution = DocIdResolver.Resolve(Docs, "Operational Resilience");

        // A draft/final pair shares this fragment — guessing between them is the wrong move.
        Assert.Null(resolution.Match);
        Assert.Equal(["A1", "A2"], resolution.Ambiguous.Select(d => d.DocId).Order());
    }

    [Fact]
    public void Resolve_UnknownIdentifier_ReturnsNone()
    {
        DocIdResolution resolution = DocIdResolver.Resolve(Docs, "Z9");

        Assert.Null(resolution.Match);
        Assert.Empty(resolution.Ambiguous);
    }
}
