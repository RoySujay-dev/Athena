using System.Collections.Concurrent;
using Athena.Filters;
using Athena.Plugins;
using Athena.Retrieval;
using Microsoft.SemanticKernel;

namespace Athena.Eval.Metrics;

/// <summary>One grounded answer as the answer-quality metrics see it.</summary>
public sealed record QaAnswer(string Answer, IReadOnlyList<Passage> Retrieved, int Violations);

public interface IQaAnswerSource
{
    Task<QaAnswer> GetAsync(string question, CancellationToken ct = default);
}

/// <summary>
/// Memoized grounded answering for the answer-quality metrics (Faithfulness, Answer
/// Correctness, Abstention, Citation Violation Rate) — ONE model answer per question feeds
/// all four. The flow is Part C's, arm-consistent with the run's retriever: retrieve →
/// format → answer.yaml through a kernel carrying the REAL GroundingGuardFilter, so
/// violations are counted by the actual guard, not a re-implementation. The yaml prompt
/// function is itself named answer_question, which is what the guard triggers on.
/// </summary>
public sealed class QaAnswerSource : IQaAnswerSource
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<Passage>>> _retrieve;
    private readonly Kernel _guardedKernel;
    private readonly KernelFunction _answerFunction;
    private readonly RetrievedContextAccessor _accessor;
    private readonly CountingViolationLog _violations;
    private readonly ConcurrentDictionary<string, Lazy<Task<QaAnswer>>> _cache =
        new(StringComparer.Ordinal);

    public QaAnswerSource(
        Func<string, CancellationToken, Task<IReadOnlyList<Passage>>> retrieve, Kernel kernel,
        ICitationViolationLog? auditSink = null)
    {
        _retrieve = retrieve;
        _accessor = new RetrievedContextAccessor();
        // The counter feeds the metric; the optional sink writes the production jsonl so a
        // nonzero rate is auditable — §11.1 says the rate comes "from the guard log".
        _violations = new CountingViolationLog(auditSink);
        // Clone: the guard must fire on THIS source's answer calls without leaking onto the
        // shared stack kernel that also serves the summariser and reranker.
        _guardedKernel = kernel.Clone();
        _guardedKernel.FunctionInvocationFilters.Add(
            new GroundingGuardFilter(_accessor, _violations));
        _answerFunction = KernelFunctionYaml.FromPromptYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "prompts", "answer.yaml")));
    }

    public Task<QaAnswer> GetAsync(string question, CancellationToken ct = default)
        => _cache.GetOrAdd(question,
            q => new Lazy<Task<QaAnswer>>(() => AnswerAsync(q, ct))).Value;

    private async Task<QaAnswer> AnswerAsync(string question, CancellationToken ct)
    {
        IReadOnlyList<Passage> passages = await _retrieve(question, ct);
        if (passages.Count == 0)
        {
            return new QaAnswer("INSUFFICIENT_CONTEXT", passages, Violations: 0);
        }

        _accessor.Set(passages); // what the guard validates citations against
        FunctionResult result = await _answerFunction.InvokeAsync(_guardedKernel, new KernelArguments
        {
            ["question"] = question,
            ["context"] = SearchPlugin.FormatPassages(passages),
        }, ct);

        return new QaAnswer(
            result.GetValue<object>()?.ToString() ?? string.Empty,
            passages,
            _violations.CountFor(question));
    }

    /// <summary>In-memory violation counter keyed by the guard-recorded question.</summary>
    private sealed class CountingViolationLog : ICitationViolationLog
    {
        private readonly ICitationViolationLog? _auditSink;
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public CountingViolationLog(ICitationViolationLog? auditSink)
        {
            _auditSink = auditSink;
        }

        public async Task RecordAsync(CitationViolation violation, CancellationToken ct = default)
        {
            _counts.AddOrUpdate(violation.Question ?? string.Empty, 1, (_, n) => n + 1);
            if (_auditSink is not null)
            {
                await _auditSink.RecordAsync(violation, ct);
            }
        }

        public int CountFor(string question) => _counts.GetValueOrDefault(question);
    }
}
