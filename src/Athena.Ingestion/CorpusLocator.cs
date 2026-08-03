namespace Athena.Ingestion;

public static class CorpusLocator // public: Athena.Eval's composition root locates the corpus the same way
{
    /// <summary>
    /// Walk up from the current directory to the repo root so commands work from any
    /// subdirectory, not only when cwd happens to be the repository root.
    /// </summary>
    public static string? LocateCorpusDirectory()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "corpus", "manifest.json");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "corpus");
        }
        return null;
    }
}
