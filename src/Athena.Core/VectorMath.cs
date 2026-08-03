namespace Athena.Core;

/// <summary>
/// Shared vector arithmetic for the pure ranking functions (lineage detection, MMR,
/// near-duplicate resolution). Lives in Core so Ingestion and Recommendation share one
/// implementation without referencing each other.
/// </summary>
public static class VectorMath
{
    /// <summary>Cosine similarity; 0 for empty or mismatched-length inputs.</summary>
    public static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        ReadOnlySpan<float> sa = a.Span;
        ReadOnlySpan<float> sb = b.Span;
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < sa.Length; i++)
        {
            dot += (double)sa[i] * sb[i];
            normA += (double)sa[i] * sa[i];
            normB += (double)sb[i] * sb[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
