using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Athena.Eval.Metrics;

/// <summary>
/// LLM judge against the hand-written gold answer, 0–1 (§11.1). An abstention on an
/// answerable case scores 0 WITHOUT a judge call — failing to answer is simply wrong, and
/// no model is needed to grade it.
/// </summary>
public sealed class AnswerCorrectness : IMetric<QaCase>
{
    private const string PromptTemplate =
        """
        You are grading an answer against a reference answer written by a domain expert.

        Question: {{$question}}

        Reference answer (ground truth):
        ---
        {{$gold}}
        ---

        Answer under grading:
        ---
        {{$answer}}
        ---

        Score how correct the answer is against the reference on a 0.0-1.0 scale:
        1.0 = states the same facts (wording may differ; extra correct detail is fine),
        0.5 = partially correct or incomplete on the reference's key facts,
        0.0 = contradicts the reference or answers a different question.
        Ignore citation tags and style; grade factual agreement only.

        Return a JSON object: {"score": <0.0-1.0>}
        """;

    private readonly IQaAnswerSource _answers;
    private readonly Kernel _kernel;
    private readonly KernelFunction _judge;

    public AnswerCorrectness(IQaAnswerSource answers, Kernel kernel)
    {
        _answers = answers;
        _kernel = kernel;
        _judge = KernelFunctionFactory.CreateFromPrompt(
            PromptTemplate,
            new OpenAIPromptExecutionSettings { Temperature = 0, ResponseFormat = "json_object" },
            functionName: "judge_correctness");
    }

    public string Name => "AnswerCorrectness";

    public async Task<double> ComputeAsync(QaCase testCase, CancellationToken ct = default)
    {
        if (!testCase.IsAnswerable)
        {
            return double.NaN;
        }

        QaAnswer answer = await _answers.GetAsync(testCase.Question, ct);
        if (AbstentionAccuracy.Score(answer.Answer) == 1)
        {
            return 0; // abstaining on an answerable question is a wrong answer, judged for free
        }

        FunctionResult result = await _judge.InvokeAsync(_kernel, new KernelArguments
        {
            ["question"] = testCase.Question,
            ["gold"] = testCase.ExpectedAnswer,
            ["answer"] = answer.Answer,
        }, ct);

        return ParseScore(result.ToString());
    }

    internal static double ParseScore(string raw)
    {
        JsonElement root = JsonDocument.Parse(JudgeJson.StripFences(raw)).RootElement;
        return Math.Clamp(root.GetProperty("score").GetDouble(), 0, 1);
    }
}
