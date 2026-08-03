using System.Globalization;
using System.Text;
using System.Text.Json;
using Athena.Agent;
using Athena.Core.Options;
using Athena.Filters;
using Athena.Plugins;
using Athena.Recommendation;
using Athena.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Athena.Eval;

/// <summary>
/// Part E evidence run (brief §10): the six utterances through the REAL agent, with the
/// resolved function calls read back from logs/telemetry.jsonl — the TelemetryFilter is the
/// authoritative witness of what the model actually chose. One session, one thread: the
/// utterances are a conversation ("What else should I read?" only means something after an
/// earlier turn), and the interest profile is updated once per user turn at this boundary.
/// </summary>
internal static class RoutingCommand
{
    private sealed record Utterance(int Number, string Text, string Expected);

    private static readonly Utterance[] Utterances =
    [
        new(1, "What does d516 say about tolerance for disruption?", "answer_question"),
        new(2, "What else should I read?", "recommend_for_user"),
        new(3, "Give me papers like the RAPTOR one.", "more_like_this"),
        new(4, "What should I read about evaluating RAG systems?", "recommend_for_query"),
        new(5, "Summarise how graph-based RAG differs from vanilla RAG, and point me at further reading.",
            "answer_question + a recommender, both in one turn"),
        new(6, "Who won the 2019 Cricket World Cup?", "no function call (out of scope)"),
    ];

    public static async Task<int> RunAsync(CancellationToken ct)
    {
        if (!EvalCommand.TryCreateFactory(out AthenaEvalContextFactory? factory, out string? repoRoot,
                out AthenaOptions? options))
        {
            return 1;
        }

        var config = EvalConfig.Of(("chunker", "fixed"), ("docvec", "centroid"));
        using IngestedStack stack = await factory.BuildStackAsync(config, ct);

        string telemetryPath = Path.Combine(repoRoot, "logs", "telemetry.jsonl");
        Kernel kernel = BuildAgentKernel(stack, options, repoRoot, telemetryPath,
            sessionId: "routing-run", out SessionInterestProfileStore profiles);
        ChatCompletionAgent agent = AthenaAgentFactory.Create(kernel);
        AgentThread? thread = null;

        var report = new StringBuilder();
        report.AppendLine($"# Part E routing table — six §10 utterances");
        report.AppendLine();
        report.AppendLine($"Config: `{config.Describe()}` · resolved calls read from `logs/telemetry.jsonl` (TelemetryFilter)");
        report.AppendLine();
        report.AppendLine("| # | Utterance | Expected | Resolved |");
        report.AppendLine("|---|-----------|----------|----------|");
        var responses = new StringBuilder();
        foreach (Utterance utterance in Utterances)
        {
            ct.ThrowIfCancellationRequested();

            // §9.4: profile <- decay*profile + (1-decay)*embed(query), once per USER turn.
            GeneratedEmbeddings<Embedding<float>> embedded = await stack.EmbeddingGenerator
                .GenerateAsync([utterance.Text], cancellationToken: ct);
            await profiles.UpdateAsync("routing-run", embedded[0].Vector, ct: ct);

            long telemetryMark = TelemetryLineCount(telemetryPath);
            var response = new StringBuilder();
            await foreach (AgentResponseItem<ChatMessageContent> item in agent.InvokeAsync(
                [new ChatMessageContent(AuthorRole.User, utterance.Text)], thread, options: null, ct))
            {
                thread = item.Thread;
                response.Append(item.Message.Content);
            }

            IReadOnlyList<string> resolved = ReadResolvedCalls(telemetryPath, telemetryMark);
            string resolvedText = resolved.Count == 0
                ? "*(no function call)*"
                : string.Join(" → ", resolved.Select(f => $"`{f}`"));
            report.AppendLine(
                $"| {utterance.Number} | {utterance.Text} | {utterance.Expected} | {resolvedText} |");
            responses.AppendLine();
            responses.AppendLine($"**[{utterance.Number}]** {Snippet(response.ToString())}");
        }

        report.AppendLine();
        report.AppendLine("## Response snippets");
        report.Append(responses);

        string resultsDir = Path.Combine(repoRoot, "eval", "results");
        Directory.CreateDirectory(resultsDir);
        string path = Path.Combine(resultsDir,
            $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-routing.md");
        await File.WriteAllTextAsync(path, report.ToString(), new UTF8Encoding(true), ct);

        Console.WriteLine(report.ToString());
        Console.WriteLine($"Report written to {path}");
        return 0;
    }

    /// <summary>
    /// The agent kernel and its dependency graph. Plugin/filter instances are registered by
    /// the composition root because they carry session-scoped state (profile store, retrieved
    /// context); AthenaKernelFactory attaches whatever the services contain.
    /// </summary>
    private static Kernel BuildAgentKernel(IngestedStack stack, AthenaOptions options,
        string repoRoot, string telemetryPath, string sessionId,
        out SessionInterestProfileStore profiles)
    {
        var retriever = new HybridRetriever(stack.Dense, stack.Lexical,
            new ReciprocalRankFusion(), new SkPromptReranker(stack.Kernel));
        var accessor = new RetrievedContextAccessor();
        profiles = new SessionInterestProfileStore();

        var services = new ServiceCollection();
        services.AddSingleton(new SearchPlugin(retriever, accessor,
            new PageReader(stack.Chunks), stack.Docs));
        services.AddSingleton(new RecommendPlugin(
            new RecommendationScorer(), new MmrDiversifier(), new LineageDuplicateResolver(),
            profiles, stack.Docs, retriever, stack.EmbeddingGenerator, TimeProvider.System,
            sessionId));
        services.AddSingleton(new GroundingGuardFilter(accessor,
            new JsonlCitationViolationLog(Path.Combine(repoRoot, "logs", "citation-violations.jsonl"))));
        services.AddSingleton(new TelemetryFilter(new JsonlTelemetryLog(telemetryPath),
            options.OpenAI.Pricing));
        services.AddSingleton(new PiiRedactionFilter());

        return AthenaKernelFactory.Build(services, options);
    }

    private static long TelemetryLineCount(string path)
        => File.Exists(path) ? File.ReadLines(path).LongCount() : 0;

    /// <summary>Function names appended to the telemetry log since <paramref name="afterLine"/>.</summary>
    private static IReadOnlyList<string> ReadResolvedCalls(string path, long afterLine)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadLines(path)
            .Skip((int)afterLine)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("function").GetString()!)
            .ToList();
    }

    private static string Snippet(string text)
    {
        string flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 220 ? flat : flat[..220] + "...";
    }
}
