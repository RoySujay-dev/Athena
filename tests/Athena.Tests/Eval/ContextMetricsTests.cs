using Athena.Eval;
using Athena.Eval.Metrics;
using Athena.Retrieval;

namespace Athena.Tests.Eval;

public sealed class ContextMetricsTests
{
    private static Passage P(string docId, int page) =>
        new($"{docId}-p{page}", docId, $"Title of {docId}", page, "text", 0.5);

    private static QaRetrievalSource Source(params Passage[] passages)
        => new((_, _) => Task.FromResult<IReadOnlyList<Passage>>(passages));

    private static readonly QaCase GoldA1Page7 = new(
        "How is operational resilience defined?", "ability to deliver critical operations",
        GoldDocId: "A1", GoldPages: [7], IsAnswerable: true);

    [Fact]
    public async Task Recall_GoldPageRetrieved_IsOne()
    {
        var metric = new ContextRecallAtK(Source(P("B2", 3), P("A1", 7), P("A1", 2)), k: 6);

        Assert.Equal(1d, await metric.ComputeAsync(GoldA1Page7));
        Assert.Equal("ContextRecall@6", metric.Name);
    }

    [Fact]
    public async Task Recall_GoldDocButWrongPage_IsZero()
    {
        var metric = new ContextRecallAtK(Source(P("A1", 8), P("A1", 9)), k: 6);

        Assert.Equal(0d, await metric.ComputeAsync(GoldA1Page7));
    }

    [Fact]
    public async Task Recall_GoldPageOnLineageSibling_IsZero()
    {
        // A2 page 7 says nearly the same thing as A1 page 7 — strictness here is what makes
        // draft-vs-final retrieval failures visible.
        var metric = new ContextRecallAtK(Source(P("A2", 7)), k: 6);

        Assert.Equal(0d, await metric.ComputeAsync(GoldA1Page7));
    }

    [Fact]
    public async Task Recall_UnanswerableCase_IsNaN()
    {
        var metric = new ContextRecallAtK(Source(P("A1", 7)), k: 6);
        var unanswerable = new QaCase("Out of corpus?", "INSUFFICIENT_CONTEXT", "", [], IsAnswerable: false);

        Assert.True(double.IsNaN(await metric.ComputeAsync(unanswerable)));
    }

    [Fact]
    public async Task Precision_CountsOnlyGoldPagesAmongRetrieved()
    {
        // 6 retrieved, 2 from A1 p7 region on the gold page: precision = 2/6.
        var metric = new ContextPrecisionAtK(
            Source(P("A1", 7), P("A1", 7), P("A1", 3), P("B2", 1), P("C4", 4), P("A2", 7)), k: 6);

        Assert.Equal(2d / 6d, await metric.ComputeAsync(GoldA1Page7), precision: 10);
    }

    [Fact]
    public async Task Precision_NothingRetrieved_IsNaN()
    {
        var metric = new ContextPrecisionAtK(Source(), k: 6);

        Assert.True(double.IsNaN(await metric.ComputeAsync(GoldA1Page7)));
    }

    [Fact]
    public async Task RetrievalSource_MemoizesPerQuestion()
    {
        int calls = 0;
        var source = new QaRetrievalSource((_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<Passage>>([P("A1", 7)]);
        });

        var recall = new ContextRecallAtK(source, 6);
        var precision = new ContextPrecisionAtK(source, 6);
        await recall.ComputeAsync(GoldA1Page7);
        await precision.ComputeAsync(GoldA1Page7);

        Assert.Equal(1, calls); // both metrics grade the same retrieved list, one retrieval
    }
}
