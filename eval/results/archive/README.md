# Archived runs

Superseded artefacts, kept for provenance rather than reference. The results reported in
REPORT.md come from the files in the parent directory, not from here.

| File(s) | Why archived |
|---|---|
| `*-qa.csv` (8 files) | Repeated runs of one configuration against the **interim 17-case** gold set, made while tuning retrieval. Superseded by the runs against the final 25-case set. |
| `20260730-211356-ablation-chunker.csv` | The interim chunker ablation on the 17-case set. This is the run that said *fixed* — superseded and **reversed** by `20260801-064006-ablation-chunker.csv` on the 25-case set. Kept because REPORT.md §5 tells that story. |
| `20260731-053853-ablation-retriever.{csv,md}` | Retriever ablation against the 17-case set; superseded by `20260801-062138-*`. |
| `20260731-035243-routing.md`, `*-routing.txt` | Earlier routing runs, superseded by `20260801-063936-routing.md` (run after the agent instructions changed). |
| `20260730-213534-lambda-sweep-D2.txt` | An extra λ sweep on seed D2. The brief (§9.1) asks for one fixed seed; B3 is that seed and its sweep is in the parent directory. |
