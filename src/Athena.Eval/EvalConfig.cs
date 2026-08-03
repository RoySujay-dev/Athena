namespace Athena.Eval;

/// <summary>
/// One evaluated configuration as key→value pairs ("chunker"→"section", "retriever"→"dense").
/// Kept stringly-typed on purpose: every future ablation axis (brief §11.3) is a new key, not
/// a new type, and the pairs serialise directly into the CSV so a committed result always
/// names the exact configuration that produced it.
/// </summary>
public sealed record EvalConfig(IReadOnlyDictionary<string, string> Values)
{
    public static EvalConfig Of(params (string Key, string Value)[] pairs)
        => new(pairs.ToDictionary(p => p.Key, p => p.Value));

    public string Get(string key, string defaultValue)
        => Values.TryGetValue(key, out string? value) ? value : defaultValue;

    /// <summary>Deterministic "k=v;k=v" form used in CSV rows and console output.</summary>
    public string Describe()
        => string.Join(';', Values.OrderBy(p => p.Key, StringComparer.Ordinal)
                                  .Select(p => $"{p.Key}={p.Value}"));
}
