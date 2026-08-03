using System.Globalization;
using System.Text;
using Athena.Core.Records;
using Athena.Retrieval;

namespace Athena.Recommendation;

/// <summary>Everything one reason is composed from — all of it produced by the actual scoring run.</summary>
/// <param name="Recommendation">The scored candidate, including its <see cref="SignalBreakdown"/>.</param>
/// <param name="PublishedOn">Publication date, for the recency clause.</param>
/// <param name="DocChunkHits">THIS document's chunk hits from the scoring retrieval, rank order — pages feed the "pp. 3-9" clause.</param>
/// <param name="SuppressedSiblings">Lineage siblings the dedup resolver collapsed into this survivor.</param>
/// <param name="ContextTopics">Topics of the seed document (more_like_this) or empty.</param>
/// <param name="RecentQueryCount">Number of recent session queries behind a profile-driven recommendation; 0 when not profile-driven.</param>
/// <param name="Weights">Scorer weights used, for dominance arithmetic; defaults to the scorer's defaults.</param>
public sealed record ReasonInputs(
    Recommendation Recommendation,
    DateTimeOffset PublishedOn,
    IReadOnlyList<Passage> DocChunkHits,
    IReadOnlyList<DocRecord> SuppressedSiblings,
    IReadOnlyList<string> ContextTopics,
    int RecentQueryCount,
    RecommendationScorerOptions? Weights = null);

/// <summary>
/// Composes the §9.5 one-line reason from the signals that actually produced the ranking —
/// never from anything the scorer didn't compute. A reason generated without reference to the
/// scoring signals is decoration; every clause here traces to a field of
/// <see cref="SignalBreakdown"/>, a retrieved chunk hit, a topic overlap, or dedup provenance,
/// and <see cref="FormatBreakdown"/> renders the numbers that back it for the telemetry log.
/// Pure function: no I/O, no clock, no model.
/// </summary>
public static class ReasonGenerator
{
    /// <summary>Returns the recommendation with its composed reason attached.</summary>
    public static Recommendation WithReason(ReasonInputs inputs)
        => inputs.Recommendation with { Reason = Compose(inputs) };

    public static string Compose(ReasonInputs inputs)
    {
        RecommendationScorerOptions weights = inputs.Weights ?? RecommendationScorerOptions.Default;
        SignalBreakdown signals = inputs.Recommendation.Signals;

        // Which signal DOMINATED is judged on weighted contributions — the same arithmetic
        // that ranked the document — not on raw signal values, which live on different scales.
        double chunkContribution = weights.ChunkAggregateWeight * signals.ChunkAggregate;
        double docSimContribution = weights.DocSimWeight * signals.DocSim;
        double recencyContribution = weights.RecencyWeight * signals.Recency;

        var reason = new StringBuilder();
        // Tie order chunk > docSim > recency: a tie means the more specific evidence wins.
        if (signals.ChunkHits > 0
            && chunkContribution >= docSimContribution
            && chunkContribution >= recencyContribution)
        {
            reason.Append($"Covers {TopicPhrase(inputs)} in depth " +
                $"({signals.ChunkHits} strong {(signals.ChunkHits == 1 ? "match" : "matches")}, " +
                $"{FormatPages(inputs.DocChunkHits)})");
        }
        else if (docSimContribution >= recencyContribution)
        {
            IReadOnlyList<string> overlap = TopicOverlap(inputs);
            reason.Append(overlap.Count > 0
                ? $"Strong topical overlap on {string.Join(", ", overlap)}"
                : $"Closely related to {TopicPhrase(inputs)}");
        }
        else
        {
            reason.Append($"A recent take on {TopicPhrase(inputs)} " +
                $"(published {inputs.PublishedOn.ToString("MMM yyyy", CultureInfo.InvariantCulture)})");
        }

        if (inputs.RecentQueryCount > 0)
        {
            reason.Append($", which your last {CountWord(inputs.RecentQueryCount)} " +
                $"{(inputs.RecentQueryCount == 1 ? "question" : "questions")} circled around");
        }

        reason.Append('.');

        foreach (DocRecord sibling in inputs.SuppressedSiblings)
        {
            reason.Append($" Supersedes {sibling.DocId}, " +
                $"{sibling.PublishedOn.ToString("MMM yyyy", CultureInfo.InvariantCulture)}.");
        }

        return reason.ToString();
    }

    /// <summary>
    /// Compact, greppable rendering of the numbers behind a reason, logged next to it so any
    /// reason is verifiable against the signals that produced it.
    /// </summary>
    public static string FormatBreakdown(SignalBreakdown signals, RecommendationScorerOptions? weights = null)
    {
        RecommendationScorerOptions w = weights ?? RecommendationScorerOptions.Default;
        return string.Create(CultureInfo.InvariantCulture,
            $"docSim={signals.DocSim:0.000}*{w.DocSimWeight:0.##} " +
            $"chunkAgg={signals.ChunkAggregate:0.000}*{w.ChunkAggregateWeight:0.##} " +
            $"recency={signals.Recency:0.000}*{w.RecencyWeight:0.##} " +
            $"hits={signals.ChunkHits} bestRank={signals.BestHitRank}");
    }

    /// <summary>First overlapping topic beats the doc's own first topic — the overlap is what the reader recognises.</summary>
    private static string TopicPhrase(ReasonInputs inputs)
    {
        IReadOnlyList<string> overlap = TopicOverlap(inputs);
        if (overlap.Count > 0)
        {
            return overlap[0];
        }

        return inputs.Recommendation.Topics.Count > 0 ? inputs.Recommendation.Topics[0] : "this area";
    }

    private static IReadOnlyList<string> TopicOverlap(ReasonInputs inputs)
        => inputs.Recommendation.Topics
            .Where(t => inputs.ContextTopics.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>"p. 4" for one page, "pp. 3-9" for a span — mirrors the citation style.</summary>
    private static string FormatPages(IReadOnlyList<Passage> hits)
    {
        var pages = hits.Select(h => h.PageNumber).Distinct().Order().ToList();
        return pages.Count switch
        {
            0 => "no pages",
            1 => $"p. {pages[0]}",
            _ => $"pp. {pages[0]}-{pages[^1]}",
        };
    }

    private static string CountWord(int n) => n switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five",
        6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}
