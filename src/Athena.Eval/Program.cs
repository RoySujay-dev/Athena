using Athena.Eval;

// CLI verb dispatch — same note as Athena.Ingestion: string-matching CLI verbs is fine; the
// brief's no-string-routing rule concerns user queries and kernel functions.

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return args switch
{
    ["qa", .. var rest] => await EvalCommand.RunQaAsync(
        EvalConfig.Of(
            ("chunker", OptionValue(rest, "--chunker") ?? "fixed"),
            ("docvec", OptionValue(rest, "--docvec") ?? "centroid"),
            ("retriever", OptionValue(rest, "--retriever") ?? "hybrid-rerank")),
        cts.Token),
    ["rec", .. var rest] => await EvalCommand.RunRecAsync(
        EvalConfig.Of(
            ("chunker", OptionValue(rest, "--chunker") ?? "fixed"),
            ("docvec", OptionValue(rest, "--docvec") ?? "centroid"),
            ("lambda", OptionValue(rest, "--lambda") ?? "0.7"),
            ("dedup", OptionValue(rest, "--dedup") ?? "on")),
        cts.Token),
    ["chunker-ablation", .. var rest] => await EvalCommand.RunChunkerAblationAsync(
        OptionValue(rest, "--retriever") ?? "hybrid-rerank", cts.Token),
    ["ablation", var ablationName] => await EvalCommand.RunNamedAblationAsync(ablationName, cts.Token),
    ["lambda-sweep", .. var sweepRest] => await LambdaSweepCommand.RunAsync(
        cts.Token, OptionValue(sweepRest, "--seed")),
    ["routing"] => await RoutingCommand.RunAsync(cts.Token),
    _ => Usage(),
};

static string? OptionValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Usage()
{
    Console.Error.WriteLine("Athena.Eval — Part F console harness. Writes CSV to eval/results/.");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Eval -- qa [--chunker fixed|section] [--docvec centroid|composite] [--retriever dense|bm25|hybrid|hybrid-rerank]");
    Console.Error.WriteLine("                                             run the seven QA metrics (§11.1) over eval/qa-cases.json");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Eval -- rec [--lambda 0.7] [--dedup on|off] [--docvec centroid|composite]");
    Console.Error.WriteLine("                                             run the five recommender metrics (§11.2) over eval/rec-cases.json");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Eval -- chunker-ablation [--retriever ...]");
    Console.Error.WriteLine("                                             fixed vs section chunker on the QA metrics, one CSV");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Eval -- ablation retriever|docvec|lambda|dedup");
    Console.Error.WriteLine("                                             the four §11.3 ablations; each writes its CSV + side-by-side matrix");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Eval -- routing");
    Console.Error.WriteLine("                                             run the six §10 utterances through the real agent, capture resolved calls");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Eval -- lambda-sweep");
    Console.Error.WriteLine("                                             more_like_this seeded on B3 at lambda 1.0/0.7/0.3 (§9.1 report)");
    return 2;
}
