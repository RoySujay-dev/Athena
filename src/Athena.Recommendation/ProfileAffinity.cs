using Athena.Core;

namespace Athena.Recommendation;

/// <summary>
/// How well a candidate document matches a session's interests. Pure function.
/// </summary>
public static class ProfileAffinity
{
    /// <summary>
    /// MAX cosine across the recent query vectors, not cosine to the decayed mean. Rationale
    /// (§9.4's crossover question): after 20 turns on cluster A and a switch to cluster C,
    /// the mean spends ~10 turns pointing between the two clusters — near nothing. Max over
    /// the raw recent queries scores a cluster-C candidate against the actual cluster-C
    /// queries and an A candidate against the A ones, so both interests stay recommendable
    /// during the crossover instead of neither. Falls back to the decayed mean when no recent
    /// queries exist (an old session restored from state), 0 when there is no signal at all.
    /// </summary>
    public static double Score(ReadOnlyMemory<float> candidateVector, InterestSnapshot snapshot)
    {
        double best = 0;
        bool any = false;
        foreach (ReadOnlyMemory<float> queryVector in snapshot.RecentQueries)
        {
            double sim = VectorMath.Cosine(candidateVector, queryVector);
            if (!any || sim > best)
            {
                best = sim;
                any = true;
            }
        }

        if (any)
        {
            return best;
        }

        return snapshot.Profile is { } profile ? VectorMath.Cosine(candidateVector, profile) : 0;
    }
}
