using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Athena.Retrieval;

/// <summary>
/// Reranking via an SK prompt function (§7): one LLM call grades every candidate 0–10 for
/// relevance to the query, then the topK by grade survive. One batched call (not one call per
/// candidate) — a third the latency and cost at the shortlist sizes RRF hands us, and the
/// model grades more consistently when it sees the candidates side by side.
/// </summary>
public sealed class SkPromptReranker : IReranker
{
    // Passages are ~800 tokens; ten of them fit any current context window, but cap each one
    // defensively so a single malformed mega-chunk cannot blow the prompt budget. 4000 chars
    // ≈ 1000 tokens — a truncated passage still carries enough signal to grade.
    private const int MaxPassageChars = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string PromptTemplate =
        """
        You are grading search results for relevance.

        Query: {{$query}}

        Candidate passages, each introduced by "### Candidate N":
        {{$candidates}}

        For EVERY candidate, grade how relevant the passage is to the query on a 0-10 scale:
        10 = directly and completely answers the query, 5 = on topic but partial or tangential,
        0 = unrelated. Grade only relevance to this query; ignore writing quality and length.

        Return a JSON object with exactly this shape, one entry per candidate, in any order:
        {"scores": [{"index": 1, "score": 7}, ...]}
        """;

    private readonly KernelFunction _grade;
    private readonly Kernel _kernel;

    public SkPromptReranker(Kernel kernel)
    {
        _kernel = kernel;
        _grade = KernelFunctionFactory.CreateFromPrompt(
            PromptTemplate,
            new OpenAIPromptExecutionSettings
            {
                Temperature = 0,
                ResponseFormat = "json_object",
            },
            functionName: "rerank_passages");
    }

    public async Task<IReadOnlyList<Passage>> RerankAsync(
        string query, IReadOnlyList<Passage> candidates, int topK, CancellationToken ct = default)
    {
        if (candidates.Count == 0 || topK <= 0)
        {
            return [];
        }

        FunctionResult result = await _grade.InvokeAsync(_kernel, new KernelArguments
        {
            ["query"] = query,
            ["candidates"] = FormatCandidates(candidates),
        }, ct);

        IReadOnlyDictionary<int, double> scores = ParseScores(result.ToString());

        return candidates
            .Select((passage, i) => (Passage: passage, Rank: i,
                // A candidate the model failed to grade scores 0 — treated as irrelevant rather
                // than failing the whole query over one missing JSON entry.
                Score: scores.GetValueOrDefault(i + 1)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Rank) // ties keep the fused (RRF) order — it encodes retriever consensus
            .Take(topK)
            .Select(x => x.Passage with { Score = x.Score })
            .ToList();
    }

    private static string FormatCandidates(IReadOnlyList<Passage> candidates)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
        {
            Passage p = candidates[i];
            string text = p.Text.Length <= MaxPassageChars ? p.Text : p.Text[..MaxPassageChars];
            sb.AppendLine($"### Candidate {i + 1} — {p.Title}, p.{p.PageNumber}");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Parses the model's {"scores":[{"index":n,"score":s}]} reply, 1-based indices.</summary>
    internal static IReadOnlyDictionary<int, double> ParseScores(string raw)
    {
        // json_object mode should return bare JSON, but strip code fences defensively.
        string json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = json.IndexOf('\n');
            int lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            json = json[(firstNewline + 1)..lastFence].Trim();
        }

        ScoresDto dto = JsonSerializer.Deserialize<ScoresDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("Reranker returned null JSON.");
        if (dto.Scores is not { Count: >= 1 })
        {
            throw new InvalidOperationException($"Reranker returned no scores: {raw}");
        }

        var scores = new Dictionary<int, double>(dto.Scores.Count);
        foreach (ScoreEntry entry in dto.Scores)
        {
            scores[entry.Index] = Math.Clamp(entry.Score, 0d, 10d);
        }

        return scores;
    }

    private sealed record ScoresDto(IReadOnlyList<ScoreEntry>? Scores);

    private sealed record ScoreEntry(int Index, double Score);
}
