using Athena.Ingestion;
using Athena.Ingestion.Rasterisation;

// CLI verb dispatch. (String comparison here is fine: the no-string-matching rule in the brief
// concerns routing *user queries* to kernel functions, not command-line verbs.)

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return args switch
{
    ["fetch"] => await FetchCommand.RunAsync(cts.Token),
    ["rasterise"] => await RasteriseCommand.RunAsync(cts.Token),
    // Defaults are the CHOSEN configuration (README design decisions 1 and 3): section-aware
    // chunking won Context Recall@6 0.900 vs 0.750 on the 25-case gold set, centroid doc vectors
    // won nDCG@5 0.857 vs 0.768. The flags exist so the Part F ablations can run the other arms.
    ["index", .. var rest] => await IndexCommand.RunAsync(
        chunkerName: OptionValue(rest, "--chunker") ?? "section",
        docVectorName: OptionValue(rest, "--docvec") ?? "centroid",
        cts.Token),
    _ => Usage(),
};

static string? OptionValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Usage()
{
    Console.Error.WriteLine("Athena.Ingestion");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Ingestion -- fetch       download the corpus PDFs listed in corpus/manifest.json");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Ingestion -- rasterise   manufacture corpus/A1-scanned.pdf (image-only, 200 DPI) from A1.pdf");
    Console.Error.WriteLine("  dotnet run --project src/Athena.Ingestion -- index [--chunker section|fixed] [--docvec centroid|composite]");
    Console.Error.WriteLine("                                                           extract, chunk, embed, summarise and index the corpus");
    return 2;
}
