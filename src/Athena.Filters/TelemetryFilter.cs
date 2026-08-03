using System.Diagnostics;
using System.Text.Json;
using Athena.Core.Options;
using Microsoft.SemanticKernel;

namespace Athena.Filters;

/// <summary>
/// One model-resolved function call: which function the model chose, with what arguments, how
/// long it ran, and the token usage of the model turn that chose it. This is the Part E
/// routing evidence — the resolved calls for the six §10 utterances come straight from here.
/// </summary>
public sealed record TelemetryEvent(
    string Function,
    IReadOnlyDictionary<string, string?> Arguments,
    double DurationMs,
    int RequestIndex,
    int FunctionIndex,
    string? ToolCallId,
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCostUsd);

public interface ITelemetryLog
{
    Task RecordAsync(TelemetryEvent telemetryEvent, CancellationToken ct = default);
}

/// <summary>Appends one JSON object per event to logs/telemetry.jsonl; stamps UTC time at the I/O boundary.</summary>
public sealed class JsonlTelemetryLog : ITelemetryLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlTelemetryLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public async Task RecordAsync(TelemetryEvent telemetryEvent, CancellationToken ct = default)
    {
        string line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            function = telemetryEvent.Function,
            arguments = telemetryEvent.Arguments,
            durationMs = Math.Round(telemetryEvent.DurationMs, 1),
            requestIndex = telemetryEvent.RequestIndex,
            functionIndex = telemetryEvent.FunctionIndex,
            toolCallId = telemetryEvent.ToolCallId,
            inputTokens = telemetryEvent.InputTokens,
            outputTokens = telemetryEvent.OutputTokens,
            estimatedCostUsd = telemetryEvent.EstimatedCostUsd,
        }, JsonOptions);

        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>
/// Function-call tracing at the interception point (hard constraint 10: instrumentation lives
/// in SK filters, never Stopwatches in business logic — this Stopwatch IS the filter). Fires
/// once per model-chosen function call during FunctionChoiceBehavior.Auto() execution, which
/// makes its log the authoritative record of how each utterance actually routed.
/// </summary>
public sealed class TelemetryFilter : IAutoFunctionInvocationFilter
{
    private readonly ITelemetryLog _log;
    private readonly PricingOptions _pricing;

    public TelemetryFilter(ITelemetryLog log, PricingOptions? pricing = null)
    {
        _log = log;
        _pricing = pricing ?? new PricingOptions();
    }

    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context,
                                                    Func<AutoFunctionInvocationContext, Task> next)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var arguments = context.Arguments?.ToDictionary(
                kv => kv.Key, kv => kv.Value?.ToString(), StringComparer.Ordinal)
                ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            (int? input, int? output) = ExtractUsage(context.ChatMessageContent);

            await _log.RecordAsync(new TelemetryEvent(
                context.Function.Name,
                arguments,
                stopwatch.Elapsed.TotalMilliseconds,
                context.RequestSequenceIndex,
                context.FunctionSequenceIndex,
                context.ToolCallId,
                input,
                output,
                EstimateCostUsd(input, output, _pricing)), context.CancellationToken);
        }
    }

    /// <summary>
    /// Estimated (not billed) USD cost of the model turn from list prices in configuration.
    /// Null — rather than a misleading $0.00 — when usage was not surfaced or prices were not
    /// configured. Pure so the arithmetic is unit-testable.
    /// </summary>
    internal static decimal? EstimateCostUsd(int? inputTokens, int? outputTokens, PricingOptions pricing)
    {
        if (inputTokens is null && outputTokens is null)
        {
            return null;
        }

        if (pricing.InputUsdPerMillionTokens == 0m && pricing.OutputUsdPerMillionTokens == 0m)
        {
            return null;
        }

        return ((inputTokens ?? 0) * pricing.InputUsdPerMillionTokens
              + (outputTokens ?? 0) * pricing.OutputUsdPerMillionTokens) / 1_000_000m;
    }

    /// <summary>
    /// Token usage of the model turn that emitted this tool call, when the connector surfaced
    /// it. Read defensively via the metadata bag — the concrete usage type differs per
    /// connector, and telemetry must never take the request down with it.
    /// </summary>
    private static (int? Input, int? Output) ExtractUsage(ChatMessageContent? message)
    {
        if (message?.Metadata is not { } metadata || !metadata.TryGetValue("Usage", out object? usage))
        {
            return (null, null);
        }

        return usage switch
        {
            OpenAI.Chat.ChatTokenUsage u => (u.InputTokenCount, u.OutputTokenCount),
            _ => (null, null),
        };
    }
}
