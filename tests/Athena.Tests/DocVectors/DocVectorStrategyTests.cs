using Athena.Core.Records;
using Athena.Ingestion.DocVectors;
using Athena.Ingestion.Enrichment;

namespace Athena.Tests.DocVectors;

public sealed class DocVectorStrategyTests
{
    [Fact]
    public void Centroid_is_the_normalised_mean()
    {
        ReadOnlyMemory<float>[] vectors =
        [
            new float[] { 1f, 0f },
            new float[] { 0f, 1f },
        ];

        ReadOnlyMemory<float> centroid = CentroidStrategy.NormalisedMean(vectors);

        // Mean is (0.5, 0.5); normalised it is (1/sqrt2, 1/sqrt2).
        float expected = (float)(1.0 / Math.Sqrt(2));
        Assert.Equal(expected, centroid.Span[0], precision: 5);
        Assert.Equal(expected, centroid.Span[1], precision: 5);
    }

    [Fact]
    public void Centroid_of_identical_vectors_is_that_vector()
    {
        ReadOnlyMemory<float>[] vectors =
        [
            new float[] { 0.6f, 0.8f },
            new float[] { 0.6f, 0.8f },
            new float[] { 0.6f, 0.8f },
        ];

        ReadOnlyMemory<float> centroid = CentroidStrategy.NormalisedMean(vectors);

        Assert.Equal(0.6f, centroid.Span[0], precision: 5);
        Assert.Equal(0.8f, centroid.Span[1], precision: 5);
    }

    [Fact]
    public void Centroid_of_no_vectors_is_empty()
    {
        Assert.True(CentroidStrategy.NormalisedMean([]).IsEmpty);
    }

    [Fact]
    public void Composite_text_carries_title_topics_and_summary()
    {
        var doc = new DocRecord
        {
            DocId = "A1",
            Title = "Principles for Operational Resilience — BCBS d516",
            Topics = ["operational resilience", "banking regulation", "basel committee"],
            Summary = "Final principles for operational resilience.",
        };

        string text = CompositeStrategy.BuildCompositeText(doc);

        Assert.Contains("d516", text);
        Assert.Contains("operational resilience, banking regulation, basel committee", text);
        Assert.Contains("Final principles", text);
    }

    [Fact]
    public void Summariser_parse_accepts_plain_and_fenced_json()
    {
        const string plain = """{"summary":"A survey.","topics":["rag","survey"]}""";
        const string fenced = "```json\n{\"summary\":\"A survey.\",\"topics\":[\"rag\",\"survey\"]}\n```";

        Assert.Equal("A survey.", SkDocumentSummariser.Parse(plain).Summary);
        Assert.Equal(["rag", "survey"], SkDocumentSummariser.Parse(fenced).Topics);
    }

    [Fact]
    public void Summariser_parse_rejects_incomplete_payloads()
    {
        Assert.Throws<InvalidOperationException>(
            () => SkDocumentSummariser.Parse("""{"summary":"","topics":[]}"""));
    }

    [Fact]
    public void Summariser_parse_enforces_the_DocRecord_contract()
    {
        // The prompt asks for <=150 words and 3-6 topics; the model is free to ignore both,
        // so the seam enforces it (brief §6.1).
        string longSummary = string.Join(' ', Enumerable.Repeat("word", 200));
        string json = $$"""
            {"summary":"{{longSummary}}","topics":["a","b","c","d","e","f","g","h"]}
            """;

        DocumentSummary parsed = SkDocumentSummariser.Parse(json);

        Assert.Equal(151, parsed.Summary.Split(' ').Length); // 150 words + the […] marker
        Assert.EndsWith("[…]", parsed.Summary);
        Assert.Equal(6, parsed.Topics.Count);
    }

    [Fact]
    public void Summariser_parse_leaves_compliant_payloads_untouched()
    {
        DocumentSummary parsed = SkDocumentSummariser.Parse(
            """{"summary":"Short and legal.","topics":["rag","survey","evaluation"]}""");

        Assert.Equal("Short and legal.", parsed.Summary);
        Assert.Equal(3, parsed.Topics.Count);
    }
}
