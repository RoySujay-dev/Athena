using Athena.Retrieval;

namespace Athena.Tests.Retrieval;

/// <summary>
/// The LLM call itself is not unit-testable; the deterministic half of the reranker — parsing
/// the model's JSON grades — is.
/// </summary>
public sealed class SkPromptRerankerTests
{
    [Fact]
    public void ParseScores_PlainJson_ReturnsIndexedScores()
    {
        var scores = SkPromptReranker.ParseScores(
            """{"scores": [{"index": 1, "score": 7}, {"index": 2, "score": 3.5}]}""");

        Assert.Equal(2, scores.Count);
        Assert.Equal(7d, scores[1]);
        Assert.Equal(3.5d, scores[2]);
    }

    [Fact]
    public void ParseScores_FencedJson_IsStripped()
    {
        var scores = SkPromptReranker.ParseScores(
            "```json\n{\"scores\": [{\"index\": 1, \"score\": 9}]}\n```");

        Assert.Equal(9d, scores[1]);
    }

    [Fact]
    public void ParseScores_OutOfRangeScores_AreClampedToZeroTen()
    {
        var scores = SkPromptReranker.ParseScores(
            """{"scores": [{"index": 1, "score": 15}, {"index": 2, "score": -3}]}""");

        Assert.Equal(10d, scores[1]);
        Assert.Equal(0d, scores[2]);
    }

    [Fact]
    public void ParseScores_EmptyOrMissingScores_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => SkPromptReranker.ParseScores("""{"scores": []}"""));
        Assert.Throws<InvalidOperationException>(() => SkPromptReranker.ParseScores("{}"));
    }
}
