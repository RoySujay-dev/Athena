using System.Text.Json;

namespace Athena.Eval;

/// <summary>Loads the hand-labelled recommender gold set from eval/rec-cases.json (brief §11.2).</summary>
public static class RecCaseSet
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<RecCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        await using FileStream stream = File.OpenRead(path);
        CaseFile file = await JsonSerializer.DeserializeAsync<CaseFile>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException($"{path} deserialised to null.");
        if (file.Cases is not { Count: > 0 })
        {
            throw new InvalidOperationException($"{path} contains no cases.");
        }

        foreach (RecCase c in file.Cases)
        {
            if (string.IsNullOrEmpty(c.SeedDocId) || c.RelevantDocIds is not { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Rec case '{c.SeedDocId}' must name a seed and at least one relevant document.");
            }
        }

        return file.Cases;
    }

    private sealed record CaseFile(IReadOnlyList<RecCase>? Cases);
}
