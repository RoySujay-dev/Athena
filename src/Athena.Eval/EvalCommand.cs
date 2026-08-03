using Athena.Core.Options;
using Athena.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Athena.Eval;

/// <summary>
/// Composition root for the eval CLI verbs. Secrets come from user-secrets only (hard
/// constraint 3) — the csproj shares Athena.Ingestion's UserSecretsId so one
/// <c>dotnet user-secrets set</c> serves both executables.
/// </summary>
internal static class EvalCommand
{
    public static async Task<int> RunQaAsync(EvalConfig config, CancellationToken ct)
    {
        if (!TryCreateHarness(out EvalHarness? harness, out string? repoRoot, config))
        {
            return 1;
        }

        IReadOnlyList<QaCase> cases = await QaCaseSet.LoadAsync(
            Path.Combine(repoRoot, "eval", "qa-cases.json"), ct);
        EvalReport report = await harness.RunQaAsync(cases, ct);
        Print(report);
        return 0;
    }

    public static async Task<int> RunRecAsync(EvalConfig config, CancellationToken ct)
    {
        if (!TryCreateHarness(out EvalHarness? harness, out string? repoRoot, config))
        {
            return 1;
        }

        IReadOnlyList<RecCase>? cases = await TryLoadRecCasesAsync(repoRoot, ct);
        if (cases is null)
        {
            return 1;
        }

        EvalReport report = await harness.RunRecommenderAsync(cases, ct);
        Print(report);
        return 0;
    }

    /// <summary>
    /// The four §11.3 ablations by name. Every grid varies exactly one axis and holds the rest
    /// at the ablation baseline (fixed chunker, centroid vectors) — one CSV + one side-by-side
    /// Markdown matrix per ablation, ready for the interpretation paragraph.
    /// NOTE: the SHIPPED chunker is section-aware; the chunker ablation reversed that choice on
    /// the final gold set (README design decision 1). The baseline stays fixed deliberately, so
    /// the four ablations remain comparable with each other and with the committed CSVs.
    /// </summary>
    public static async Task<int> RunNamedAblationAsync(string name, CancellationToken ct)
    {
        (string, string)[] baseline = [("chunker", "fixed"), ("docvec", "centroid")];
        EvalConfig[] configs;
        bool needsRecCases;
        switch (name)
        {
            case "retriever": // ablation 1 → QA metrics
                configs = new[] { "dense", "bm25", "hybrid", "hybrid-rerank" }
                    .Select(r => EvalConfig.Of([.. baseline, ("retriever", r)])).ToArray();
                needsRecCases = false;
                break;
            case "docvec": // ablation 2 → nDCG@5 (rec metrics)
                configs = new[] { "centroid", "composite" }
                    .Select(v => EvalConfig.Of(("chunker", "fixed"), ("docvec", v))).ToArray();
                needsRecCases = true;
                break;
            case "lambda": // ablation 3 → nDCG@5 + Intra-List Diversity
                configs = new[] { "1.0", "0.7", "0.3" }
                    .Select(l => EvalConfig.Of([.. baseline, ("lambda", l)])).ToArray();
                needsRecCases = true;
                break;
            case "dedup": // ablation 4 → Duplicate Leakage + nDCG@5
                configs = new[] { "on", "off" }
                    .Select(d => EvalConfig.Of([.. baseline, ("dedup", d)])).ToArray();
                needsRecCases = true;
                break;
            default:
                Console.Error.WriteLine($"Unknown ablation '{name}' (use retriever|docvec|lambda|dedup).");
                return 2;
        }

        if (!TryCreateHarness(out EvalHarness? harness, out string? repoRoot, configs[0]))
        {
            return 1;
        }

        IReadOnlyList<QaCase> qaCases = [];
        IReadOnlyList<RecCase> recCases = [];
        if (needsRecCases)
        {
            IReadOnlyList<RecCase>? loaded = await TryLoadRecCasesAsync(repoRoot, ct);
            if (loaded is null)
            {
                return 1;
            }

            recCases = loaded;
        }
        else
        {
            qaCases = await QaCaseSet.LoadAsync(Path.Combine(repoRoot, "eval", "qa-cases.json"), ct);
        }

        EvalReport report = await harness.RunAblationAsync(
            new AblationGrid(name, configs, qaCases, recCases), ct);

        string matrix = AblationReportFormatter.BuildMatrix(report);
        string matrixPath = Path.ChangeExtension(report.CsvPath, ".md");
        await File.WriteAllTextAsync(matrixPath, matrix, new System.Text.UTF8Encoding(true), ct);

        Console.WriteLine();
        Console.WriteLine(matrix);
        Console.WriteLine($"CSV: {report.CsvPath}");
        Console.WriteLine($"Matrix: {matrixPath}");
        return 0;
    }

    private static async Task<IReadOnlyList<RecCase>?> TryLoadRecCasesAsync(
        string repoRoot, CancellationToken ct)
    {
        string casesPath = Path.Combine(repoRoot, "eval", "rec-cases.json");
        if (!File.Exists(casesPath))
        {
            Console.Error.WriteLine(
                $"{casesPath} not found. Label the 8 recommender seeds (brief §11.2) first: " +
                "{ \"cases\": [{ \"seedDocId\": \"B3\", \"relevantDocIds\": [\"B1\", ...] }, ...] }");
            return null;
        }

        return await RecCaseSet.LoadAsync(casesPath, ct);
    }

    public static async Task<int> RunChunkerAblationAsync(string retriever, CancellationToken ct)
    {
        if (!TryCreateHarness(out EvalHarness? harness, out string? repoRoot,
                EvalConfig.Of(("chunker", "fixed"), ("retriever", retriever))))
        {
            return 1;
        }

        IReadOnlyList<QaCase> cases = await QaCaseSet.LoadAsync(
            Path.Combine(repoRoot, "eval", "qa-cases.json"), ct);
        var grid = new AblationGrid(
            "chunker",
            [
                EvalConfig.Of(("chunker", "fixed"), ("docvec", "centroid"), ("retriever", retriever)),
                EvalConfig.Of(("chunker", "section"), ("docvec", "centroid"), ("retriever", retriever)),
            ],
            cases,
            RecCases: []);
        EvalReport report = await harness.RunAblationAsync(grid, ct);
        Print(report);
        return 0;
    }

    private static bool TryCreateHarness(
        out EvalHarness harness, out string repoRoot, EvalConfig defaultConfig)
    {
        harness = null!;
        if (!TryCreateFactory(out AthenaEvalContextFactory factory, out repoRoot))
        {
            return false;
        }

        harness = new EvalHarness(factory, defaultConfig, Path.Combine(repoRoot, "eval", "results"));
        return true;
    }

    /// <summary>Shared secrets/corpus gate for every eval verb (harness runs and the lambda sweep).</summary>
    internal static bool TryCreateFactory(out AthenaEvalContextFactory factory, out string repoRoot)
        => TryCreateFactory(out factory, out repoRoot, out _);

    /// <summary>Overload exposing the bound options — the routing run builds the agent kernel from them.</summary>
    internal static bool TryCreateFactory(
        out AthenaEvalContextFactory factory, out string repoRoot, out AthenaOptions boundOptions)
    {
        factory = null!;
        repoRoot = null!;
        boundOptions = null!;

        string? corpusDir = CorpusLocator.LocateCorpusDirectory();
        if (corpusDir is null)
        {
            Console.Error.WriteLine("Could not locate corpus/manifest.json above the current directory.");
            return false;
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(EvalCommand).Assembly)
            .Build();
        var options = new AthenaOptions();
        configuration.Bind(options);
        if (string.IsNullOrEmpty(options.OpenAI.ApiKey)
            || string.IsNullOrEmpty(options.AzureDocumentIntelligence.Endpoint))
        {
            Console.Error.WriteLine(
                "Missing secrets. Set OpenAI:{ApiKey,ChatModelId,EmbeddingModel} and " +
                "AzureDocumentIntelligence:{Endpoint,ApiKey} via 'dotnet user-secrets' " +
                "(from src/Athena.Ingestion or src/Athena.Eval — they share one store).");
            return false;
        }

        ILoggerFactory loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information));

        repoRoot = Path.GetDirectoryName(corpusDir)!;
        factory = new AthenaEvalContextFactory(options, corpusDir, loggerFactory);
        boundOptions = options;
        return true;
    }

    private static void Print(EvalReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {report.Kind} ({report.StartedAtUtc:yyyy-MM-dd HH:mm:ss}Z) ===");
        foreach (EvalRun run in report.Runs)
        {
            Console.WriteLine($"-- {run.Config.Describe()}");
            foreach ((string metric, double mean) in run.Aggregates.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"   {metric,-22} {mean:0.000}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"CSV: {report.CsvPath}");
    }
}
