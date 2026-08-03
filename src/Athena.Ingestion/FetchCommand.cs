using System.Text;

namespace Athena.Ingestion;

/// <summary>
/// <c>dotnet run --project src/Athena.Ingestion -- fetch</c>: download every manifest PDF into
/// corpus/. PDFs are never committed (brief §4.2); this command is the reproducible substitute.
/// </summary>
public static class FetchCommand
{
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var corpusDir = CorpusLocator.LocateCorpusDirectory();
        if (corpusDir is null)
        {
            Console.Error.WriteLine("error: could not find corpus/manifest.json walking up from the current directory.");
            Console.Error.WriteLine("       Run from the repository root: dotnet run --project src/Athena.Ingestion -- fetch");
            return 2;
        }

        var manifest = await CorpusManifest.LoadAsync(Path.Combine(corpusDir, "manifest.json"), ct);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // Some publisher hosts reject requests with no User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Athena-corpus-fetch/0.1");

        int fetched = 0, skipped = 0;
        var failures = new List<(string Id, string Reason)>();

        foreach (var doc in manifest.Documents)
        {
            var target = Path.Combine(corpusDir, $"{doc.Id}.pdf");

            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                Console.WriteLine($"  skip  {doc.Id,-3} already present ({new FileInfo(target).Length:N0} bytes)");
                skipped++;
                continue;
            }

            try
            {
                var bytes = await http.GetByteArrayAsync(doc.Url, ct);

                if (bytes.Length == 0)
                {
                    failures.Add((doc.Id, "downloaded zero bytes"));
                    continue;
                }
                // A server error page saved as .pdf would poison ingestion silently; the magic
                // number check catches that class of failure at fetch time.
                if (bytes.Length < 4 || Encoding.ASCII.GetString(bytes, 0, 4) != "%PDF")
                {
                    failures.Add((doc.Id, $"response is not a PDF ({bytes.Length:N0} bytes; check the URL in a browser)"));
                    continue;
                }

                await File.WriteAllBytesAsync(target, bytes, ct);
                Console.WriteLine($"  fetch {doc.Id,-3} {bytes.Length:N0} bytes  <- {doc.Url}");
                fetched++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user cancelled: stop everything, not just this document
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                failures.Add((doc.Id, e.Message));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"fetched {fetched}, skipped {skipped}, failed {failures.Count} of {manifest.Documents.Count} documents -> {corpusDir}");
        foreach (var (id, reason) in failures)
            Console.Error.WriteLine($"  FAILED {id}: {reason}");

        return failures.Count == 0 ? 0 : 1;
    }
}
