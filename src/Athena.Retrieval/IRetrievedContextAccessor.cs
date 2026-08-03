namespace Athena.Retrieval;

/// <summary>
/// The passages retrieved for the current turn, exposed so the Part C grounding guard
/// (brief §8.1) can validate answer_question's citations against what was ACTUALLY retrieved
/// — without answer_question knowing the guard exists. Like IInterestProfileStore, this is
/// scoped per session/turn state: register it scoped, never as a singleton.
/// </summary>
public interface IRetrievedContextAccessor
{
    IReadOnlyList<Passage> Current { get; }

    void Set(IReadOnlyList<Passage> passages);
}

/// <inheritdoc />
public sealed class RetrievedContextAccessor : IRetrievedContextAccessor
{
    public IReadOnlyList<Passage> Current { get; private set; } = [];

    public void Set(IReadOnlyList<Passage> passages) => Current = passages;
}
