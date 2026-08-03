using Microsoft.ML.Tokenizers;

namespace Athena.Ingestion.Chunking;

/// <summary>
/// Counts tokens the way the embedding model will. A seam rather than a direct tokenizer
/// dependency so chunker unit tests can substitute a trivial counter and run without the
/// cl100k vocabulary data.
/// </summary>
public interface ITokenCounter
{
    int CountTokens(string text);
}

/// <summary>
/// cl100k_base counter — the vocabulary of the OpenAI text-embedding-3-* models, so the
/// chunkers' "~800 tokens" is measured in the same units the embedding model actually sees.
/// </summary>
public sealed class Cl100kTokenCounter : ITokenCounter
{
    // TiktokenTokenizer instances are thread-safe and expensive to build; share one.
    private static readonly TiktokenTokenizer Tokenizer =
        TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public int CountTokens(string text) => Tokenizer.CountTokens(text);
}
