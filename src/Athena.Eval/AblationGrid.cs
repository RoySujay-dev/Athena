namespace Athena.Eval;

/// <summary>
/// One ablation (brief §11.3): a named axis and the configurations to compare along it. The
/// harness evaluates the SAME cases under every configuration and writes one CSV holding all
/// arms, so a committed result file is self-contained evidence for the README paragraph.
/// </summary>
public sealed record AblationGrid(
    string Name,
    IReadOnlyList<EvalConfig> Configs,
    IReadOnlyList<QaCase> QaCases,
    IReadOnlyList<RecCase> RecCases);
