using Athena.Agent;
using Athena.Filters;
using Athena.Plugins;
using Athena.Recommendation;
using Athena.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Rec = Athena.Recommendation.Recommendation;

namespace Athena.Web.Services;

/// <summary>One user turn: the question, the streamed answer, and its evidence.</summary>
public sealed class ChatTurn
{
    public required string User { get; init; }
    public string Assistant { get; set; } = string.Empty;
    public bool IsComplete { get; set; }

    /// <summary>Retrieval snapshot backing this turn's citations (from the context accessor).</summary>
    public IReadOnlyList<Passage> Passages { get; set; } = [];

    public TurnMetrics? Metrics { get; set; }
}

/// <summary>
/// Per-circuit chat state: its own kernel, agent thread, interest profile, retrieved-context
/// accessor, and telemetry sink. Scoped per Blazor circuit — never shared (the profile store
/// contract, project code style). The shared read-mostly stack (collections, Lucene index,
/// embedding cache) comes from the singleton <see cref="CorpusService"/>; documents ingested
/// mid-session are visible to the next question because retrieval reads those live stores.
/// </summary>
public sealed class ChatSession
{
    private readonly CorpusService _corpus;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private ChatCompletionAgent? _agent;
    private AgentThread? _thread;
    private SessionInterestProfileStore? _profiles;
    private RetrievedContextAccessor? _accessor;
    private SessionTelemetrySink? _telemetry;
    private SidebarRecommender? _sidebar;

    public ChatSession(CorpusService corpus) => _corpus = corpus;

    public List<ChatTurn> Turns { get; } = [];

    public IReadOnlyList<Rec> Recommendations { get; private set; } = [];

    public bool IsStreaming { get; private set; }

    public void Initialize()
    {
        if (_agent is not null)
        {
            return;
        }

        if (_corpus.FailureReason is not null)
        {
            throw new InvalidOperationException(_corpus.FailureReason);
        }

        _accessor = new RetrievedContextAccessor();
        _profiles = new SessionInterestProfileStore();
        _telemetry = new SessionTelemetrySink(new JsonlTelemetryLog(
            Path.Combine(_corpus.RepoRoot, "logs", "telemetry.jsonl")));

        var retriever = new HybridRetriever(_corpus.Dense, _corpus.Lexical,
            new ReciprocalRankFusion(), new SkPromptReranker(_corpus.Kernel));
        var dedup = new LineageDuplicateResolver();
        var diversifier = new MmrDiversifier();
        _sidebar = new SidebarRecommender(_profiles, _corpus.Docs, dedup, diversifier, _sessionId);

        // Same composition-root shape as the Part E routing run: plugin/filter INSTANCES are
        // registered here because they carry this session's state; the factory attaches them.
        var services = new ServiceCollection();
        services.AddSingleton(new SearchPlugin(retriever, _accessor,
            new PageReader(_corpus.Chunks), _corpus.Docs));
        services.AddSingleton(new RecommendPlugin(
            new RecommendationScorer(), diversifier, dedup, _profiles, _corpus.Docs, retriever,
            _corpus.EmbeddingGenerator, TimeProvider.System, _sessionId));
        services.AddSingleton(new GroundingGuardFilter(_accessor, new JsonlCitationViolationLog(
            Path.Combine(_corpus.RepoRoot, "logs", "citation-violations.jsonl"))));
        services.AddSingleton(new TelemetryFilter(_telemetry, _corpus.Options.OpenAI.Pricing));
        services.AddSingleton(new PiiRedactionFilter());

        Kernel kernel = AthenaKernelFactory.Build(services, _corpus.Options);
        _agent = AthenaAgentFactory.Create(kernel);
    }

    /// <summary>
    /// Runs one user turn: profile update, streamed agent invocation (deltas surface through
    /// <paramref name="onDelta"/>), then the turn's evidence — citation passages from the
    /// accessor, metrics from the telemetry sink, refreshed sidebar recommendations.
    /// </summary>
    public async Task SendAsync(string userText, Func<Task> onDelta, CancellationToken ct = default)
    {
        if (_agent is null || IsStreaming)
        {
            return;
        }

        var turn = new ChatTurn { User = userText };
        Turns.Add(turn);
        IsStreaming = true;
        int telemetryMark = _telemetry!.Mark();
        try
        {
            // §9.4: profile <- decay*profile + (1-decay)*embed(query), exactly ONCE per user
            // turn at this boundary — one turn can invoke two functions (§10 utterance 5),
            // so per-function updates inside the plugin would double-count the query.
            GeneratedEmbeddings<Embedding<float>> embedded = await _corpus.EmbeddingGenerator
                .GenerateAsync([userText], cancellationToken: ct);
            await _profiles!.UpdateAsync(_sessionId, embedded[0].Vector, ct: ct);

            _accessor!.Set([]);
            await foreach (AgentResponseItem<StreamingChatMessageContent> item in
                _agent.InvokeStreamingAsync(
                    [new ChatMessageContent(AuthorRole.User, userText)], _thread,
                    options: null, ct))
            {
                _thread = item.Thread;
                if (!string.IsNullOrEmpty(item.Message.Content))
                {
                    turn.Assistant += item.Message.Content;
                    await onDelta();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            turn.Assistant += $"\n\n[error: {ex.GetBaseException().Message}]";
        }
        finally
        {
            turn.Passages = _accessor!.Current;
            turn.Metrics = _telemetry.AggregateSince(telemetryMark);
            turn.IsComplete = true;
            IsStreaming = false;
        }

        Recommendations = await _sidebar!.RecommendAsync(topK: 5, ct);
        await onDelta();
    }
}
