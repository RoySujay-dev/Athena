using System.Text.Json;

namespace Athena.Filters;

/// <summary>
/// Appends one JSON object per violation to logs/citation-violations.jsonl (brief §8.1).
/// The UTC timestamp is stamped here, at the I/O boundary, so the filter itself stays
/// clock-free and deterministic under test.
/// </summary>
public sealed class JsonlCitationViolationLog : ICitationViolationLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlCitationViolationLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public async Task RecordAsync(CitationViolation violation, CancellationToken ct = default)
    {
        string line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            function = violation.FunctionName,
            citedTitle = violation.CitedTitle,
            citedPage = violation.CitedPage,
            question = violation.Question,
        }, JsonOptions);

        // Serialise writers: concurrent turns (Blazor sessions) must not interleave half-lines.
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
