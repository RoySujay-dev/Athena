namespace Athena.Eval;

/// <summary>
/// The metrics (and the retrieval stack behind them) for one configuration. Owns disposable
/// resources built for the run (Lucene index, embedding generator).
/// </summary>
public sealed class EvalContext : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _resources;

    public EvalContext(
        IReadOnlyList<IMetric<QaCase>> qaMetrics,
        IReadOnlyList<IMetric<RecCase>> recMetrics,
        IReadOnlyList<IDisposable> resources)
    {
        QaMetrics = qaMetrics;
        RecMetrics = recMetrics;
        _resources = resources;
    }

    public IReadOnlyList<IMetric<QaCase>> QaMetrics { get; }

    public IReadOnlyList<IMetric<RecCase>> RecMetrics { get; }

    public void Dispose()
    {
        foreach (IDisposable resource in _resources)
        {
            resource.Dispose();
        }
    }
}

/// <summary>
/// Builds the full evaluated system for one configuration. The harness stays pure
/// orchestration; everything expensive (ingestion, index building, LLM wiring) lives behind
/// this factory, which is also what lets ablations rebuild the system per arm.
/// </summary>
public interface IEvalContextFactory
{
    Task<EvalContext> CreateAsync(EvalConfig config, CancellationToken ct = default);
}
