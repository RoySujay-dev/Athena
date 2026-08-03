using System.Text.Json;

namespace Athena.Eval;

/// <summary>Loads the hand-written QA gold set from eval/qa-cases.json (brief §11.1).</summary>
public static class QaCaseSet
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<QaCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        await using FileStream stream = File.OpenRead(path);
        CaseFile file = await JsonSerializer.DeserializeAsync<CaseFile>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException($"{path} deserialised to null.");
        if (file.Cases is not { Count: > 0 })
        {
            throw new InvalidOperationException($"{path} contains no cases.");
        }

        foreach (QaCase c in file.Cases.Where(c => c.IsAnswerable))
        {
            if (string.IsNullOrEmpty(c.GoldDocId) || c.GoldPages.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Answerable case '{c.Question}' must name a gold document and pages.");
            }
        }

        return file.Cases;
    }

    private sealed record CaseFile(IReadOnlyList<QaCase>? Cases);
}
