using System.Text.Json;
using Athena.Filters;

namespace Athena.Tests.Filters;

public sealed class JsonlCitationViolationLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"athena-guard-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Record_AppendsOneParseableJsonObjectPerViolation()
    {
        string path = Path.Combine(_dir, "logs", "citation-violations.jsonl");
        var log = new JsonlCitationViolationLog(path);

        await log.RecordAsync(new CitationViolation("answer_question", "Some Doc", 99, "why?"));
        await log.RecordAsync(new CitationViolation("answer_question", "Other Doc", 3, null));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        JsonElement first = JsonDocument.Parse(lines[0]).RootElement; // throws if not valid JSON
        Assert.Equal("Some Doc", first.GetProperty("citedTitle").GetString());
        Assert.Equal(99, first.GetProperty("citedPage").GetInt32());
        Assert.Equal("why?", first.GetProperty("question").GetString());
        Assert.True(first.TryGetProperty("timestampUtc", out _));
    }
}
