using Athena.Core.Records;
using Athena.Eval;
using Athena.Eval.Metrics;

namespace Athena.Tests.Eval;

/// <summary>Toy-input tests for the pure scoring cores of the §11.2 recommender metrics.</summary>
public sealed class RecMetricTests
{
    private static IReadOnlySet<string> Set(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Ndcg_HandComputed_RelevantAtRanksOneAndThree()
    {
        // relevant {A,B}, list [A, x, B, y, z]:
        //   DCG  = 1/log2(2) + 1/log2(4) = 1 + 0.5          = 1.5
        //   IDCG = 1/log2(2) + 1/log2(3) = 1 + 0.63093      = 1.63093
        //   nDCG = 1.5 / 1.63093                             = 0.91972
        double ndcg = NdcgAtK.Compute(["A", "x", "B", "y", "z"], Set("A", "B"), k: 5);

        Assert.Equal(0.91972, ndcg, precision: 5);
    }

    [Fact]
    public void Ndcg_PerfectPrefix_IsOne_AndNoRelevant_IsZero()
    {
        Assert.Equal(1.0, NdcgAtK.Compute(["A", "B", "c"], Set("A", "B"), k: 5), precision: 10);
        Assert.Equal(0.0, NdcgAtK.Compute(["x", "y"], Set("A"), k: 5));
    }

    [Fact]
    public void Ndcg_KCapsBothSides()
    {
        // k=1: only rank 1 counts. Relevant doc at rank 2 is invisible.
        Assert.Equal(0.0, NdcgAtK.Compute(["x", "A"], Set("A"), k: 1));
        Assert.Equal(1.0, NdcgAtK.Compute(["A", "x"], Set("A", "B", "C"), k: 1), precision: 10);
    }

    [Fact]
    public void Ndcg_EmptyRelevantSet_IsNaN()
        => Assert.True(double.IsNaN(NdcgAtK.Compute(["A"], Set(), k: 5)));

    [Fact]
    public void Mrr_FirstRelevantAtRankThree_IsOneThird()
    {
        Assert.Equal(1.0 / 3, MeanReciprocalRank.Compute(["x", "y", "A"], Set("A", "B")), precision: 10);
        Assert.Equal(1.0, MeanReciprocalRank.Compute(["A"], Set("A")), precision: 10);
        Assert.Equal(0.0, MeanReciprocalRank.Compute(["x", "y"], Set("A")));
    }

    [Fact]
    public void IntraListDiversity_IdenticalVectorsZero_OrthogonalOne()
    {
        ReadOnlyMemory<float> x = new float[] { 1f, 0f };
        ReadOnlyMemory<float> y = new float[] { 0f, 1f };

        Assert.Equal(0.0, IntraListDiversity.Compute([x, x]), precision: 6);
        Assert.Equal(1.0, IntraListDiversity.Compute([x, y]), precision: 6);
        // Trio {x, x, y}: pairwise cosines 1, 0, 0 → mean 1/3 → ILD = 2/3.
        Assert.Equal(2.0 / 3, IntraListDiversity.Compute([x, x, y]), precision: 6);
        Assert.True(double.IsNaN(IntraListDiversity.Compute([x])));
    }

    [Fact]
    public void DuplicateLeakage_FiresOnlyWhenTwoListMembersShareAGroup()
    {
        DocRecord Doc(string id, string? group) => new() { DocId = id, LineageGroup = group };

        Assert.Equal(1, DuplicateLeakage.Compute([Doc("A1", "A1"), Doc("A2", "A1")]));
        Assert.Equal(0, DuplicateLeakage.Compute([Doc("A1", "A1"), Doc("C4", "C4")]));
        Assert.Equal(0, DuplicateLeakage.Compute([Doc("B1", null), Doc("B2", null)])); // null never groups
    }

    [Fact]
    public async Task CatalogueCoverage_IsCumulative_AndAggregateReturnsTheFinalValue()
    {
        // 21-doc corpus; seed1 recommends {A,B,C}, seed2 recommends {C,D,E,F,G,H} → 8 distinct.
        var source = new FakeRecommendationSource(corpusSize: 21, new()
        {
            ["s1"] = ["A", "B", "C"],
            ["s2"] = ["C", "D", "E", "F", "G", "H"],
        });
        var coverage = new CatalogueCoverage(source);

        double afterFirst = await coverage.ComputeAsync(new RecCase("s1", ["A"]));
        double afterSecond = await coverage.ComputeAsync(new RecCase("s2", ["A"]));

        Assert.Equal(3.0 / 21, afterFirst, precision: 10);
        Assert.Equal(8.0 / 21, afterSecond, precision: 10);
        Assert.Equal(8.0 / 21, coverage.Aggregate([afterFirst, afterSecond]), precision: 10);
    }

    private sealed class FakeRecommendationSource(
        int corpusSize, Dictionary<string, string[]> lists) : IRecommendationSource
    {
        public int CorpusSize => corpusSize;

        public Task<IReadOnlyList<DocRecord>> GetAsync(string seedDocId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DocRecord>>(
                lists[seedDocId].Select(id => new DocRecord { DocId = id }).ToList());
    }
}
