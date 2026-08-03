using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Athena.Eval.Metrics;

/// <summary>
/// LLM judge (§11.1): the fraction of the answer's factual claims supported by the retrieved
/// context. Claim-counting (supported/total) rather than a single holistic grade — the ratio
/// forces the judge to enumerate before scoring, which is measurably harder to inflate.
/// NaN for unanswerable cases and for abstentions (an unmade claim cannot be unfaithful).
/// </summary>
public sealed class Faithfulness : IMetric<QaCase>
{
    private const string PromptTemplate =
        """
        You are auditing a grounded answer for faithfulness to its sources.

        Source passages:
        ---
        {{$context}}
        ---

        Answer under audit:
        ---
        {{$answer}}
        ---

        List every distinct factual claim the answer makes, then decide for each whether the
        source passages FULLY support it. A claim is supported only if the passages state it;
        plausible-but-absent is unsupported. Ignore the answer's citation tags themselves.

        Return a JSON object: {"total": <number of claims>, "supported": <number supported>}
        """;

    private readonly IQaAnswerSource _answers;
    private readonly Kernel _kernel;
    private readonly KernelFunction _judge;

    public Faithfulness(IQaAnswerSource answers, Kernel kernel)
    {
        _answers = answers;
        _kernel = kernel;
        _judge = KernelFunctionFactory.CreateFromPrompt(
            PromptTemplate,
            new OpenAIPromptExecutionSettings { Temperature = 0, ResponseFormat = "json_object" },
            functionName: "judge_faithfulness");
    }

    public string Name => "Faithfulness";

    public async Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
    {
        if (!testCase.IsAnswerable)
        {
            return double.NaN;
        }

        QaAnswer answer = await _answers.GetAsync(testCase.Question, ct);
        if (AbstentionAccuracy.Score(answer.Answer) == 1)
        {
            return double.NaN; // abstained: no claims exist to audit
        }

        FunctionResult result = await _judge.InvokeAsync(_kernel, new KernelArguments
        {
            ["context"] = string.Join("\n---\n", answer.Retrieved.Select(p => p.Text)),
            ["answer"] = answer.Answer,
        }, ct);

        return ParseClaimRatio(result.ToString());
    }

    /// <summary>supported/total from the judge's JSON, clamped; NaN when no claims counted.</summary>
    internal static double ParseClaimRatio(string raw)
    {
        JsonElement root = JsonDocument.Parse(JudgeJson.StripFences(raw)).RootElement;
        double total = root.GetProperty("total").GetDouble();
        double supported = root.GetProperty("supported").GetDouble();
        if (total <= 0)
        {
            return double.NaN;
        }

        return Math.Clamp(supported / total, 0, 1);
    }
}

/// <summary>Shared defensive handling of judge output (json_object mode should be bare JSON).</summary>
internal static class JudgeJson
{
    internal static string StripFences(string raw)
    {
        string json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = json.IndexOf('\n');
            int lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            json = json[(firstNewline + 1)..lastFence].Trim();
        }

        return json;
    }
}
