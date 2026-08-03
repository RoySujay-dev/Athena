using Athena.Filters;

namespace Athena.Web.Services;

/// <summary>
/// Per-turn aggregate of the TelemetryFilter's events — what the metrics strip shows. All
/// figures come from the filter (hard constraint 10): latency is the summed duration of the
/// turn's resolved function calls, tokens/cost are summed over the model turns that chose
/// them. A turn that resolved no function (out-of-scope decline) legitimately has no metrics.
/// </summary>
public sealed record TurnMetrics(
    IReadOnlyList<string> Functions,
    double LatencyMs,
    int InputTokens,
    int OutputTokens,
    decimal? CostUsd)
{
    public bool HasAny => Functions.Count > 0;
}

/// <summary>
/// Session-scoped <see cref="ITelemetryLog"/> that keeps the events in memory for the UI while
/// forwarding every one to the shared logs/telemetry.jsonl writer — the strip and the on-disk
/// evidence log always agree because they are fed by the same filter callback.
/// </summary>
public sealed class SessionTelemetrySink : ITelemetryLog
{
    private readonly ITelemetryLog _inner;
    private readonly List<TelemetryEvent> _events = [];
    private readonly object _gate = new();

    public SessionTelemetrySink(ITelemetryLog inner) => _inner = inner;

    public async Task RecordAsync(TelemetryEvent telemetryEvent, CancellationToken ct = default)
    {
        await _inner.RecordAsync(telemetryEvent, ct);
        lock (_gate)
        {
            _events.Add(telemetryEvent);
        }
    }

    /// <summary>Call at the start of a user turn; pass the mark to <see cref="AggregateSince"/> after it.</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _events.Count;
        }
    }

    public TurnMetrics AggregateSince(int mark)
    {
        List<TelemetryEvent> turn;
        lock (_gate)
        {
            turn = _events.Skip(mark).ToList();
        }

        decimal? cost = turn.Any(e => e.EstimatedCostUsd is not null)
            ? turn.Sum(e => e.EstimatedCostUsd ?? 0m)
            : null;
        return new TurnMetrics(
            turn.Select(e => e.Function).ToList(),
            turn.Sum(e => e.DurationMs),
            turn.Sum(e => e.InputTokens ?? 0),
            turn.Sum(e => e.OutputTokens ?? 0),
            cost);
    }
}
