using Athena.Core.Records;
using Athena.Recommendation;

namespace Athena.Tests.Recommendation;

/// <summary>
/// Unit vectors at known angles make every cosine — and therefore every greedy MMR pick —
/// hand-computable. Seed is (1,0) throughout; cos(angle between) is the similarity.
/// </summary>
public sealed class MmrDiversifierTests
{
    private static readonly ReadOnlyMemory<float> Seed = new float[] { 1f, 0f };

    private static DocRecord Doc(string id, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        return new DocRecord
        {
            DocId = id,
            Title = $"Title of {id}",
            Embedding = new[] { (float)Math.Cos(radians), (float)Math.Sin(radians) },
        };
    }

    private readonly MmrDiversifier _mmr = new();

    [Fact]
    public void Lambda1_IsPureRelevanceOrder()
    {
        // Seed similarities: A=cos10°≈0.9848 > B=cos12°≈0.9781 > C=cos20°≈0.9397.
        var candidates = new[] { Doc("C", -20), Doc("A", 10), Doc("B", 12) };

        var selected = _mmr.Select(Seed, candidates, topK: 3, lambda: 1.0);

        Assert.Equal(["A", "B", "C"], selected.Select(d => d.DocId));
    }

    [Fact]
    public void Lambda0_PicksMostRelevantFirst_ThenFarthestFromSelected()
    {
        // D1 at 0° (sim 1.0), D2 at 10°, D3 at 90°, D4 at 45°.
        // First pick (all scores tie at 0): tie-break on seed similarity → D1.
        // Then score = -max sim(d, selected):
        //   round 2: D2 -cos10°≈-0.985, D4 -cos45°≈-0.707, D3 -cos90°=0        → D3
        //   round 3: D2 -max(cos10°,cos80°)=-0.985, D4 -max(cos45°,cos45°)=-0.707 → D4
        var candidates = new[] { Doc("D2", 10), Doc("D1", 0), Doc("D4", 45), Doc("D3", 90) };

        var selected = _mmr.Select(Seed, candidates, topK: 4, lambda: 0.0);

        Assert.Equal(["D1", "D3", "D4", "D2"], selected.Select(d => d.DocId));
    }

    [Fact]
    public void Lambda07_DiversityOverridesRelevanceForTheSecondPick()
    {
        // A at 10° is picked first. B at 12° is more seed-similar than C at -20°, but B is
        // nearly a clone of A. Hand-worked second round:
        //   B: 0.7·cos12° − 0.3·cos2°  = 0.68470 − 0.29982 = 0.38489
        //   C: 0.7·cos20° − 0.3·cos30° = 0.65778 − 0.25981 = 0.39797  → C wins
        var candidates = new[] { Doc("A", 10), Doc("B", 12), Doc("C", -20) };

        var selected = _mmr.Select(Seed, candidates, topK: 3, lambda: 0.7);

        Assert.Equal(["A", "C", "B"], selected.Select(d => d.DocId));
    }

    [Fact]
    public void IdenticalCandidates_TieBreakByDocIdOrdinal_IsDeterministic()
    {
        var candidates = new[] { Doc("B", 30), Doc("A", 30) };

        var selected = _mmr.Select(Seed, candidates, topK: 2, lambda: 0.7);

        Assert.Equal(["A", "B"], selected.Select(d => d.DocId));
    }

    [Fact]
    public void TopKBeyondCandidateCount_ReturnsAllInSelectionOrder()
    {
        var candidates = new[] { Doc("A", 10), Doc("B", 80) };

        var selected = _mmr.Select(Seed, candidates, topK: 10, lambda: 0.7);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void EmptyCandidates_ReturnsEmpty()
    {
        Assert.Empty(_mmr.Select(Seed, [], topK: 5));
    }

    [Fact]
    public void LambdaOutsideZeroOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _mmr.Select(Seed, [Doc("A", 0)], 1, lambda: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _mmr.Select(Seed, [Doc("A", 0)], 1, lambda: 1.1));
    }

    [Fact]
    public void InputListIsNeverMutated()
    {
        var candidates = new[] { Doc("B", 40), Doc("A", 10), Doc("C", 90) };
        var original = candidates.Select(d => d.DocId).ToArray();

        _mmr.Select(Seed, candidates, topK: 3, lambda: 0.0);

        Assert.Equal(original, candidates.Select(d => d.DocId));
    }
}
