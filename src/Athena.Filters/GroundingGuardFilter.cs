using System.Text.RegularExpressions;
using Athena.Retrieval;
using Microsoft.SemanticKernel;

namespace Athena.Filters;

/// <summary>
/// Post-hoc citation validation (brief §8.1). Registered as an IFunctionInvocationFilter so it
/// sees the return value of answer_question without answer_question knowing it exists. Every
/// [Title, p.N] in the answer is asserted against the passage set ACTUALLY retrieved this turn
/// (scoped <see cref="IRetrievedContextAccessor"/>); unsupported ones are stripped, a warning
/// is appended, and each strip is recorded to logs/citation-violations.jsonl.
///
/// POLICY: strip-and-warn, not fail-the-turn. One hallucinated tag rarely means the whole
/// answer is bad — the supported sentences keep their audited citations, the reader is told
/// exactly what was removed, and the violation still counts in full against the Part F rate,
/// so the failure is surfaced rather than swallowed. The counter-argument is real: a stripped
/// sentence now stands UNCITED, violating rule 1, and an uncited-but-confident claim may earn
/// more trust than the citation it lost; in a compliance setting where a wrong answer is worse
/// than no answer, failing the turn outright is the defensible choice. We accept that cost
/// because this corpus's failure mode is subtle (draft-cited-as-final), and a hard fail would
/// discard the correctly-cited majority of the answer that makes the error findable at all.
/// </summary>
public sealed partial class GroundingGuardFilter : IFunctionInvocationFilter
{
    private const string GuardedFunction = "answer_question";

    private readonly IRetrievedContextAccessor _context;
    private readonly ICitationViolationLog _log;

    public GroundingGuardFilter(IRetrievedContextAccessor context, ICitationViolationLog log)
    {
        _context = context;
        _log = log;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context,
                                                Func<FunctionInvocationContext, Task> next)
    {
        await next(context);
        if (!string.Equals(context.Function.Name, GuardedFunction, StringComparison.Ordinal))
        {
            return;
        }

        string answer = context.Result.GetValue<object>()?.ToString() ?? string.Empty;
        IReadOnlyList<Citation> unsupported = FindUnsupported(answer, _context.Current);
        if (unsupported.Count == 0)
        {
            return;
        }

        foreach (Citation citation in unsupported)
        {
            context.Arguments.TryGetValue("question", out object? question);
            await _log.RecordAsync(
                new CitationViolation(GuardedFunction, citation.Title, citation.Page,
                    question?.ToString()),
                context.CancellationToken);
        }

        context.Result = new FunctionResult(context.Result, StripAndWarn(answer, unsupported));
    }

    /// <summary>A [Title, p.N] tag as it appeared in the answer.</summary>
    internal readonly record struct Citation(string Title, int Page, string Tag);

    // [Title, p.N] with no nested brackets or newlines in the title. Anything the regex does
    // not match is not a citation and is none of the guard's business.
    [GeneratedRegex(@"\[([^\[\]\n]+?),\s*p\.(\d+)\]")]
    private static partial Regex CitationTag();

    internal static IReadOnlyList<Citation> ParseCitations(string answer)
        => CitationTag().Matches(answer)
            .Select(m => new Citation(m.Groups[1].Value.Trim(), int.Parse(m.Groups[2].Value), m.Value))
            .ToList();

    /// <summary>
    /// Shortest cited title accepted as an abbreviation. Below this, a "match" is too easy —
    /// ("Principles", p.2) should not validate against anything.
    /// </summary>
    private const int MinAbbreviatedTitleLength = 10;

    internal static IReadOnlyList<Citation> FindUnsupported(
        string answer, IReadOnlyList<Passage> retrieved)
        => ParseCitations(answer)
            .Where(c => !IsSupported(c, retrieved))
            .ToList();

    /// <summary>
    /// A citation is supported when it UNIQUELY resolves against the retrieved set: exact
    /// title match (case-insensitive — a casing slip is not a hallucination), or a title
    /// PREFIX that matches exactly one retrieved document on the cited page. Measured on the
    /// live corpus: models reliably abbreviate the long BCBS titles ("Principles for
    /// Operational Resilience (final, Mar 2021) — BCBS d516" → "Principles for Operational
    /// Resilience"), and counting that as a violation floods the Part F rate with format
    /// noise. Uniqueness is what keeps the draft/final trap armed: when BOTH lineage members
    /// were retrieved, their shared prefix resolves to two documents, stays ambiguous, and is
    /// stripped — an abbreviation may never smuggle a draft past as its final.
    /// </summary>
    private static bool IsSupported(Citation citation, IReadOnlyList<Passage> retrieved)
    {
        // Span match: an ~800-token chunk runs across ~2 pages, and the model legitimately
        // cites content sitting after the chunk's start page (measured live: p.7 cited from a
        // chunk starting p.6 — full exact title, page inside the chunk). A cited page is
        // plausible for a passage when it falls in [PageNumber, EndPage]; EndPage 0 (older
        // records, fabricated fixtures) degrades to exact start-page matching.
        var pageMatches = retrieved
            .Where(p => citation.Page >= p.PageNumber
                        && citation.Page <= Math.Max(p.PageNumber, p.EndPage))
            .ToList();
        if (pageMatches.Any(p => string.Equals(p.Title, citation.Title, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (citation.Title.Length < MinAbbreviatedTitleLength)
        {
            return false;
        }

        var prefixDocs = pageMatches
            .Where(p => p.Title.StartsWith(citation.Title, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.DocId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return prefixDocs.Count == 1;
    }

    internal static string StripAndWarn(string answer, IReadOnlyList<Citation> unsupported)
    {
        string stripped = answer;
        foreach (Citation citation in unsupported.DistinctBy(c => c.Tag))
        {
            stripped = stripped.Replace(citation.Tag, string.Empty, StringComparison.Ordinal);
        }

        string tags = string.Join(", ", unsupported.Select(c => c.Tag).Distinct());
        return $"{stripped.TrimEnd()}\n\n[warning: removed {unsupported.Count} citation(s) not " +
               $"supported by the retrieved passages: {tags}]";
    }
}
