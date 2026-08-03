using Athena.Filters;
using Athena.Retrieval;
using Microsoft.SemanticKernel;

namespace Athena.Tests.Filters;

/// <summary>
/// The guard is exercised through a real Kernel invoking a canned method function, so the
/// filter runs exactly the way SK runs it in production — nothing here needs a live model.
/// </summary>
public sealed class GroundingGuardFilterTests
{
    private sealed class InMemoryViolationLog : ICitationViolationLog
    {
        public List<CitationViolation> Recorded { get; } = [];

        public Task RecordAsync(CitationViolation violation, CancellationToken ct = default)
        {
            Recorded.Add(violation);
            return Task.CompletedTask;
        }
    }

    private static readonly Passage RealDocP7 = new(
        "c1", "A1", "Real Doc", 7, "supported passage text", 0.9);

    private static (Kernel Kernel, InMemoryViolationLog Log) BuildKernel(
        string cannedAnswer, params Passage[] retrieved)
    {
        var accessor = new RetrievedContextAccessor();
        accessor.Set(retrieved);
        var log = new InMemoryViolationLog();
        Kernel kernel = Kernel.CreateBuilder().Build();
        kernel.FunctionInvocationFilters.Add(new GroundingGuardFilter(accessor, log));
        kernel.Plugins.AddFromFunctions("qa",
        [
            KernelFunctionFactory.CreateFromMethod(() => cannedAnswer, "answer_question"),
            KernelFunctionFactory.CreateFromMethod(() => cannedAnswer, "hybrid_search"),
        ]);
        return (kernel, log);
    }

    [Fact]
    public async Task InducedFailure_UnsupportedCitation_IsStrippedAndLogged()
    {
        // The brief's credibility test (§8.1): "zero violations" is only credible if the
        // detector demonstrably fires on a deliberately induced failure. [Some Doc, p.99]
        // exists in no retrieved passage — the guard MUST strip it and MUST log it.
        var (kernel, log) = BuildKernel(
            "A claim [Real Doc, p.7]. A hallucinated claim [Some Doc, p.99].", RealDocP7);

        FunctionResult result = await kernel.InvokeAsync("qa", "answer_question",
            new KernelArguments { ["question"] = "the induced question" });
        string answer = result.GetValue<object>()!.ToString()!;

        // The warning deliberately names the stripped tag, so split answer body from warning
        // before asserting the tag is gone from the body.
        int warningStart = answer.IndexOf("[warning:", StringComparison.Ordinal);
        Assert.True(warningStart > 0, $"expected an appended warning, got: {answer}");
        string body = answer[..warningStart];
        Assert.DoesNotContain("[Some Doc, p.99]", body);
        Assert.Contains("[Real Doc, p.7]", body); // supported citation untouched
        Assert.Contains("warning: removed 1 citation(s)", answer);
        Assert.Contains("[Some Doc, p.99]", answer[warningStart..]);

        CitationViolation violation = Assert.Single(log.Recorded);
        Assert.Equal("Some Doc", violation.CitedTitle);
        Assert.Equal(99, violation.CitedPage);
        Assert.Equal("the induced question", violation.Question);
    }

    [Fact]
    public async Task FullySupportedAnswer_PassesThroughUntouched_AndLogsNothing()
    {
        string original = "A claim [Real Doc, p.7]. Another [Real Doc, p.7].";
        var (kernel, log) = BuildKernel(original, RealDocP7);

        FunctionResult result = await kernel.InvokeAsync("qa", "answer_question");

        Assert.Equal(original, result.GetValue<object>()!.ToString());
        Assert.Empty(log.Recorded);
    }

    [Fact]
    public async Task OtherFunctions_AreNotGuarded()
    {
        // hybrid_search returns passages, not an answer — the guard must ignore it even when
        // its output happens to contain citation-shaped text.
        var (kernel, log) = BuildKernel("[Some Doc, p.99] passage text");

        FunctionResult result = await kernel.InvokeAsync("qa", "hybrid_search");

        Assert.Contains("[Some Doc, p.99]", result.GetValue<object>()!.ToString());
        Assert.Empty(log.Recorded);
    }

    [Fact]
    public async Task InsufficientContext_HasNoCitations_GuardStaysSilent()
    {
        var (kernel, log) = BuildKernel("INSUFFICIENT_CONTEXT");

        FunctionResult result = await kernel.InvokeAsync("qa", "answer_question");

        Assert.Equal("INSUFFICIENT_CONTEXT", result.GetValue<object>()!.ToString());
        Assert.Empty(log.Recorded);
    }

    [Fact]
    public void FindUnsupported_PageMustMatchExactly_TitleIsCaseInsensitive()
    {
        var unsupported = GroundingGuardFilter.FindUnsupported(
            "One [real doc, p.7]. Two [Real Doc, p.8].", [RealDocP7]);

        GroundingGuardFilter.Citation citation = Assert.Single(unsupported);
        Assert.Equal(8, citation.Page); // casing slip is fine; wrong page is a violation
    }

    [Fact]
    public void FindUnsupported_UniqueTitlePrefix_IsAcceptedAsAbbreviation()
    {
        // Models abbreviate "Principles for Operational Resilience (final, Mar 2021) — BCBS
        // d516" to its head; a prefix resolving to exactly one retrieved doc is supported.
        var final = new Passage("c1", "A1",
            "Principles for Operational Resilience (final, Mar 2021) — BCBS d516", 11, "t", 1.0);

        Assert.Empty(GroundingGuardFilter.FindUnsupported(
            "Claim [Principles for Operational Resilience, p.11].", [final]));
    }

    [Fact]
    public void FindUnsupported_PrefixSharedByDraftAndFinal_StaysAmbiguous_AndIsStripped()
    {
        // BOTH lineage members retrieved on the cited page: the shared prefix resolves to two
        // documents — the abbreviation cannot smuggle a draft past as its final.
        var final = new Passage("c1", "A1",
            "Principles for Operational Resilience (final, Mar 2021) — BCBS d516", 11, "t", 1.0);
        var draft = new Passage("c2", "A2",
            "Principles for Operational Resilience (consultative draft, Aug 2020) — BCBS d509", 11, "t", 1.0);

        var unsupported = GroundingGuardFilter.FindUnsupported(
            "Claim [Principles for Operational Resilience, p.11].", [final, draft]);

        Assert.Single(unsupported);
    }

    [Fact]
    public void FindUnsupported_CitedPageInsideTheChunkSpan_IsSupported()
    {
        // Chunk starts p.6, runs to p.8: citing p.7 is legitimate; p.9 is past the span.
        var spanning = new Passage("c1", "A1", "Real Doc", 6, "t", 1.0, EndPage: 8);

        Assert.Empty(GroundingGuardFilter.FindUnsupported("Claim [Real Doc, p.7].", [spanning]));
        Assert.Empty(GroundingGuardFilter.FindUnsupported("Claim [Real Doc, p.8].", [spanning]));
        Assert.Single(GroundingGuardFilter.FindUnsupported("Claim [Real Doc, p.9].", [spanning]));
        Assert.Single(GroundingGuardFilter.FindUnsupported("Claim [Real Doc, p.5].", [spanning]));
    }

    [Fact]
    public void FindUnsupported_TinyPrefix_IsNeverAcceptedAsAbbreviation()
    {
        var final = new Passage("c1", "A1", "Principles for Operational Resilience", 11, "t", 1.0);

        Assert.Single(GroundingGuardFilter.FindUnsupported("Claim [Princ, p.11].", [final]));
    }

    [Fact]
    public void ParseCitations_FindsEveryTag_AndIgnoresNonCitationBrackets()
    {
        var citations = GroundingGuardFilter.ParseCitations(
            "See [Doc One, p.3] and [Doc Two, p.14]; but [not a citation] and [p.5] are ignored.");

        Assert.Equal(2, citations.Count);
        Assert.Equal(("Doc One", 3), (citations[0].Title, citations[0].Page));
        Assert.Equal(("Doc Two", 14), (citations[1].Title, citations[1].Page));
    }
}
