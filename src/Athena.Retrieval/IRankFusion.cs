namespace Athena.Retrieval;

/// <summary>Merges several ranked lists of passages into one ranked list (§7).</summary>
public interface IRankFusion
{
    IReadOnlyList<Passage> Fuse(IReadOnlyList<IReadOnlyList<Passage>> rankedLists);
}
