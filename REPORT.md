# Athena — Evaluation Report (Part F)

All numbers were produced by `dotnet run --project src/Athena.Eval -- <verb>` and are committed
as CSV (plus an auto-generated Markdown matrix per ablation) under `eval/results/`. Every table
below transcribes a committed file, named beneath it.

## 1. What was measured, and against what

**Two configurations appear in this report, deliberately.** The **shipped** configuration is
`chunker=section; docvec=centroid; retriever=hybrid-rerank`, MMR `lambda=0.7`, dedup `on` — §2 and
§3 report it. The **ablation baseline** is identical except `chunker=fixed`; §4 and §6–§8 hold
that constant so the four §11.3 ablations stay comparable with each other and with every CSV
committed before the chunker decision reversed (§5). Where this matters, it is flagged inline.

**QA gold set** — `eval/qa-cases.json`, 25 hand-written cases: 20 answerable, 5 unanswerable. Five
answerable cases come from cluster D (NIST/CISA), so the system is graded on a topic the retrieval
stack was not tuned against. Three are draft-vs-final catches covering both BCBS lineage pairs:
qa02 (answer differs between A1 final and A2 draft), qa05 (only from the A2 draft), qa20 (only
from the A4 draft). Gold pages are physical PDF pages, each verified against extracted text.

One deliberate conservatism: a gold page names the page that *states* the fact, not every page
alluding to it, so reported recall slightly **under**-estimates (D5 restates its five-pillar list
on p10 as well as the labelled p6). Two other candidates were checked and rejected: A1 p9/p10 use
"deliver critical operations through disruption" without defining it, and A1 p9's "Principle 6"
cross-references a *different* document.

**Recommender gold set** — `eval/rec-cases.json`, 8 hand-labelled seeds spanning all four
clusters, four of which (B3, B4, B5, C1) straddle the deliberately-adjacent B/C boundary. One
labelling policy matters for ablation 4 and is stated in the file itself: **a document's own
superseded lineage sibling is not labelled relevant**, because an analyst asking "what should I
read after d516" is not helped by d516's own consultative draft.

## 2. QA metrics (brief §11.1)

Source: `eval/results/20260801-071321-qa.csv` — the **shipped** configuration
(`chunker=section; docvec=centroid; retriever=hybrid-rerank`).

| Metric | Value |
|---|---|
| Context Recall@6 | 0.900 |
| Context Precision@6 | 0.208 |
| Faithfulness (LLM judge) | 1.000 |
| Answer Correctness (LLM judge) | 0.975 |
| Abstention Accuracy | 1.000 |
| Citation Violation Rate | 0.000 |
| OCR Delta | 0.000 |

The ablations in §4 and §6–§8 hold `chunker=fixed` instead (baseline run:
`20260731-144546-qa.csv`, recall 0.750), so their absolute recall runs ~0.15 lower than above;
within-ablation comparisons are unaffected because both arms always share a chunker.

**Interpretation.** Recall@6 0.900 means 18 of 20 answerable questions retrieved a labelled gold
page. Only two missed, and they are different failures:

- **qa14 is the near-duplicate trap, in retrieval rather than generation.** C4 and C5 are RAGAS v1
  and v2, stating the three quality aspects in near-identical words (verified: C4 p3–5, again C5
  p4–5). The gold names C4; recall and precision are both 0.00 while the answer was judged fully
  correct — consistent with the evidence being found in the twin (inference: I did not record
  *which* document came back). The recommender handles this at document granularity via lineage
  groups; the QA path has no equivalent. **This is the most valuable finding in the QA
  evaluation** — the only miss that survives every configuration I tested.
- **qa03 (A1 Principle 6, a 12-page document)** is a plain miss with no structural excuse.

Both long-document misses that fixed-window chunking produced (D3, D5) are gone under
section-aware chunking — §5, where the same three cases flip.

**Context Precision@6 of 0.208 must be read carefully.** With topK=6 and typically one or two
relevant chunks per question, the arithmetic ceiling is ~0.17–0.33. The observed value sits inside
that band: it describes the retrieval *budget*, not ranking quality. Ablation 1 is the honest test
of ranking quality, because it varies the stack while holding the budget fixed.

**The judge metrics are not deterministic, and I have the evidence to prove it.** Faithfulness is
1.000 here and 0.975 on the fixed-chunker run; Answer Correctness is 0.975 here (one case, qa19,
at 0.5) and 1.000 there. Two runs of one *identical* configuration six minutes apart differed on
Faithfulness by the same margin. Judge-scored metrics in this report should be read as ±1 case;
the deterministic set-arithmetic metrics (recall, precision, leakage) carry the argument.

**Abstention 1.000 and Citation Violations 0.000.** All five out-of-corpus questions returned
`INSUFFICIENT_CONTEXT`, including two (PCI DSS, Kubernetes) chosen to sit *near* cluster D's
security vocabulary rather than far from everything. Zero violations is only credible because the
detector is proven to fire: `GroundingGuardFilterTests` induces an unsupported `[Title, p.N]` and
asserts it is stripped, warned about, and logged to `logs/citation-violations.jsonl`.

**OCR Delta = 0.000, and getting to that number was itself the lesson.** The metric is
`recall(scan) − recall(native)` over three applicable A1 cases, each retrieval filtered to its own
copy. Across **eleven committed runs** of the fixed-chunker baseline, ten report 0.000 and one
reports +0.333 — one case (qa03) flipping under the reranker's non-determinism. An earlier draft
of this report led on that +0.333; it was an outlier, and reporting it as a finding would have
been wrong.

The reproducible answer is **zero: DI's image-OCR path and its text-layer path retrieve
identically here.** That corroborates the extraction-time measurement that forced the per-page
classifier — A1-SCAN averaged word confidence 0.9913 vs native A1's 0.9902, 4,071 of 4,074 words
recovered (a one-off diagnostic, not a committed artefact). With one engine on both sides a null
result is what should be expected, and the brief says a small delta is itself the finding. The
methodological point is sharper: **with three applicable cases one flip is ±0.333, so the metric
cannot resolve anything smaller than a third** — a single run of it should not be trusted.

## 3. Recommender metrics (brief §11.2)

Sources: `eval/results/20260801-071819-recommender.csv` (**shipped**, `chunker=section`) and
`eval/results/20260731-143613-recommender.csv` (ablation baseline, `chunker=fixed`).

| Metric | shipped (section) | baseline (fixed) |
|---|---|---|
| nDCG@5 | 0.803 | 0.857 |
| MRR | 1.000 | 1.000 |
| Catalogue Coverage | 0.750 | 0.750 |
| Intra-List Diversity | 0.216 | 0.240 |
| Duplicate Leakage | 0.000 | 0.000 |

**Interpretation.** MRR 1.000 means all eight seeds put a relevant document first — unsurprising
with hand-authored labels, which is why nDCG@5 is the informative number: errors sit in each
list's *tail*, not its head. Coverage 0.750 means 15 of 20 recommendable documents appear across
the eight lists, so the recommender is not collapsing onto a few hub documents — the failure mode
the brief warns of when a heterogeneous corpus is centroided into a blob. Duplicate Leakage is
0.000 by construction of both the dedup method *and* the labelling policy (§8 separates those).

**The cross-regime trade-off, which I did not anticipate.** Both columns are the same recommender
over the same gold set; only the *chunker* differs. Section-aware chunking, worth +0.150 QA recall
(§5), **costs 0.054 recommender nDCG@5** and 0.024 ILD — because document vectors are centroids of
chunk embeddings, so changing chunk boundaries changes every document vector.

This is the sharpest illustration of the brief's claim that these are **two retrieval regimes
sharing one pipeline**: one upstream decision moves them in opposite directions and no
configuration maximises both. I chose QA recall — a wrong answer is worse than a mediocre reading
list — but had I only measured the recommender, I would have shipped the other chunker.

## 4. Ablation 1 — retrieval stack: dense vs BM25 vs RRF vs RRF + rerank

Source: `eval/results/20260801-062138-ablation-retriever.md` (held constant: `chunker=fixed; docvec=centroid`)

| Metric | dense | bm25 | hybrid (RRF) | hybrid + rerank |
|---|---|---|---|---|
| Context Recall@6 | 0.650 | 0.650 | 0.700 | **0.750** |
| Context Precision@6 | 0.150 | 0.183 | 0.167 | **0.192** |
| Answer Correctness | 0.950 | 0.900 | 0.875 | **0.975** |
| Faithfulness | 0.950 | **0.975** | 0.942 | **0.975** |
| Abstention Accuracy | 1.000 | 1.000 | 1.000 | 1.000 |
| Citation Violation Rate | 0.000 | 0.000 | 0.000 | 0.000 |

**Interpretation — three findings, one of them inconvenient.**

**BM25 is not ceremony.** Dense and BM25 tie on recall (0.650), but **BM25 beats dense on
precision** (0.183 vs 0.150). The brief predicted this and it held: the corpus is full of short
exact identifiers — "d516", "SP 800-207", "Principle 6", "CSF 2.0" — whose embeddings carry almost
no lexical signal. Roughly a third of the gold set asks questions of that shape; dense-only would
have failed them silently.

**Fusion adds recall; reranking converts it into answers.** RRF lifts recall 0.650 → 0.700 and
reranking again to 0.750 — +0.100, two more questions in twenty. Precision moves the same way.

**The inconvenient result: RRF *without* reranking has the worst Answer Correctness of the four
arms (0.875, below both single retrievers)** despite better recall than either. Fusion widens the
candidate pool by construction — promoting documents both retrievers rank moderately — so without
a reranker the six passages reaching the answer prompt are more topically mixed: recall rises
while *usable* context gets noisier. **RRF and the reranker are one component, not two optional
ones.** Shipping fusion alone would have been a measurable downgrade, and I'd have missed it had
I only compared hybrid against dense.

Caveat: correctness is LLM-judged, so the 0.075 gap is 1.5 scored points of 20 on a
non-deterministic judge; recall and precision carry the argument. (OCR Delta varies by arm only
because one flip is ±0.333 — it is not a property of the retrieval stack.)

## 5. Chunker choice (design decision 1) — and a result that changed my mind

Source: `eval/results/20260801-064006-ablation-chunker.csv` (held constant: `docvec=centroid; retriever=hybrid-rerank`)

| Metric | fixed-window | section-aware |
|---|---|---|
| Context Recall@6 | 0.750 | **0.900** |
| Context Precision@6 | 0.192 | **0.208** |
| Answer Correctness | **0.975** | 0.900 |
| Faithfulness | 0.975 | 0.975 |
| Abstention Accuracy | 1.000 | 1.000 |

**This is the ablation I got wrong the first time.** Against an interim 17-case gold set
(`archive/20260730-211356-ablation-chunker.csv`) the chunkers tied on recall and fixed won
marginally on precision, so I shipped fixed. Against the final 25-case set the ordering reverses:
section-aware gains **+0.150 recall** — three more questions in twenty (qa01, qa17, qa19 flip).

What changed was the *gold set*, not the chunker. The 17-case version leaned on the short BCBS
documents, which carry few `sectionHeading` labels, so section splitting had nothing to work with.
The five cluster-D cases added for the final set come from 32-to-80-page NIST/CISA standards with
strong headings — exactly where a fixed window straddling two sections dilutes both.

**The honest tension:** Answer Correctness moves the other way (0.975 → 0.900) — 1.5 scored points
of 20 on a judge whose run-to-run variance I measured at the same magnitude (§2), against a
3-case gain in deterministic set arithmetic. §3 records a second cost: the recommender loses
0.054 nDCG. I weight QA recall highest and switched the default, without claiming either cost is
noise.

**The lesson:** the evaluation set decided this, not the algorithm. A gold set under-representing
part of the corpus doesn't just add noise — it can produce a confident, reproducible, *wrong*
answer, and nothing about the 17-case result looked unreliable.

## 6. Ablation 2 — document vector: centroid vs composite

Source: `eval/results/20260731-143652-ablation-docvec.md` (held constant: `chunker=fixed`)

**Declared substitution:** the brief names this ablation "centroid vs **summary-embedding**"; the
two strategies built are centroid and **composite** (`Title + Topics + Summary`), so the tested
arm is summary-*plus-metadata*. §6.4 requires at least two strategies and composite is a superset
of the summary embedding. The truncation weakness identified below is a property of the summary
text, which both variants share.

| Metric | centroid | composite |
|---|---|---|
| nDCG@5 | **0.857** | 0.768 |
| MRR | **1.000** | 0.938 |
| Catalogue Coverage | 0.750 | **0.900** |
| Intra-List Diversity | 0.240 | **0.342** |
| Duplicate Leakage | 0.000 | 0.000 |

**Interpretation — the ablation that contradicted my expectation.** The brief predicts a centroid
turns the RAG survey (B3) into "a vector that sits near the middle of the whole corpus and is
close to nothing in particular", and I expected composite to win. It lost by 0.089 nDCG.

The reason is a cost documented in the README's deviations: the summariser sees only a document's
leading ~6,000 tokens, so for C3 (88 pp) the ≤150-word summary describes the abstract and
introduction. Composite therefore inherits a **truncation** loss, discarding late-document topics
entirely, where the centroid's **averaging** loss keeps them represented, however blurrily. Blur
beat amnesia on this corpus.

The trade runs the other way on diversity: composite wins coverage (0.900 vs 0.750) and ILD
(0.342 vs 0.240), because a summary-derived vector is a *sharper topical claim*. If the objective
were catalogue exploration rather than agreement with relevance labels, composite would be
defensible.

## 7. Ablation 3 — MMR lambda

Source: `eval/results/20260731-143725-ablation-lambda.md` (held constant: `chunker=fixed; docvec=centroid`)

| Metric | λ = 1.0 | λ = 0.7 | λ = 0.3 |
|---|---|---|---|
| nDCG@5 | **0.874** | 0.857 | 0.526 |
| Intra-List Diversity | 0.230 | 0.240 | **0.541** |
| Catalogue Coverage | **0.800** | 0.750 | 0.550 |
| MRR | 1.000 | 1.000 | 1.000 |

**Interpretation.** The trade-off the brief predicted, and it is not linear. λ=1.0 → 0.7 costs
0.017 nDCG and buys 0.010 ILD, both inside the noise of an 8-seed set. λ=0.7 → 0.3 costs a further
**0.331 nDCG** to buy 0.301 ILD: λ=0.3 is not a "more diverse" recommender but a broken one — with
~20 documents the MMR penalty dominates a similarity signal with few candidates to choose between.

The counter-intuitive column is Catalogue Coverage, which *falls* as λ falls (0.800 → 0.550) even
as diversity rises. Diversity is measured *within* a list, coverage *across* the eight: at λ=0.3
every seed is pushed toward the same handful of mutually-distant outliers, so each list looks
diverse while the union shrinks. **An intra-list diversity metric can improve while the
recommender becomes globally less varied.** MRR is 1.000 in all three arms — MMR's first pick is
pure relevance by construction, whatever λ.

### 7.1 The §9.1 fixed-seed list (seed B3, the RAG survey)

Source: `eval/results/*-lambda-sweep.txt`. Ranks 2–5 shown; rank 1 is B1 in all three arms.

| λ | 2 | 3 | 4 | 5 |
|---|---|---|---|---|
| 1.0 | C1 | C2 | B5 | C5 |
| 0.7 | C1 | C2 | C5 | B5 |
| 0.3 | **A5** | C2 | C1 | C5 |

**What changes and why.** From λ=1.0 to 0.7 the *membership* is identical and only ranks 4 and 5
swap. At λ=0.3 the list breaks: **A5, the FSI executive summary on banking operational resilience,
enters at rank 2** with document similarity 0.273 against the 0.83–0.91 of the papers it displaced.
Nothing about A5 helps someone reading a RAG survey; it is selected *because* it is unlike
everything else, which is what the MMR penalty rewards when allowed to dominate. On a 20-document
corpus spanning four unrelated clusters, "maximally marginal" quickly means "different subject
entirely" — the qualitative counterpart to the nDCG collapse above.

## 8. Ablation 4 — near-duplicate resolution on vs off

Source: `eval/results/20260731-143756-ablation-dedup.md` (held constant: `chunker=fixed; docvec=centroid`)

| Metric | dedup on | dedup off |
|---|---|---|
| Duplicate Leakage | **0.000** | 0.125 |
| nDCG@5 | **0.857** | 0.782 |
| MRR | **1.000** | 0.875 |
| Catalogue Coverage | 0.750 | **0.900** |
| Intra-List Diversity | **0.240** | 0.221 |

**Interpretation, including where my labelling helped the result.** Dedup off leaks a lineage pair
into 1 of 8 lists (0.125) and costs 0.075 nDCG and 0.125 MRR; dedup on drives leakage to zero and
improves every ranking metric.

That needs a caveat, because the brief predicts the opposite and explains why: if gold labels mark
*both* members of a pair relevant, dedup removes a document the labels call relevant and nDCG
falls. My labels deliberately exclude a seed's superseded sibling, so dedup can only help or be
neutral here. **The measurement and the labels are not independent: read this as "dedup is
consistent with my definition of a useful follow-up", not "dedup is objectively better."**

The metric dedup *costs* is Catalogue Coverage (0.900 → 0.750): suppressing A2, A4 and C5 removes
three documents from the surfaceable set. That is the price the brief asks to be named — the
lineage-metadata approach is the most accurate and least general of the three options, and part
of its cost is that a fraction of the corpus becomes permanently unrecommendable.

## 9. Part E — routing table (six §10 utterances)

Source: `eval/results/20260801-063936-routing.md`. The **Resolved** column is read back from
`logs/telemetry.jsonl` — the `IAutoFunctionInvocationFilter`'s record of what the model actually
invoked, not hand observation. All six ran as one conversation on one thread, since utterance 2
is meaningless without a turn before it.

| # | Utterance | Expected | Resolved |
|---|---|---|---|
| 1 | What does d516 say about tolerance for disruption? | `answer_question` | ✅ `answer_question` |
| 2 | What else should I read? | `recommend_for_user` | ✅ `recommend_for_user` |
| 3 | Give me papers like the RAPTOR one. | `more_like_this` | ✅ `more_like_this` |
| 4 | What should I read about evaluating RAG systems? | `recommend_for_query` | ✅ `recommend_for_query` |
| 5 | Summarise how graph-based RAG differs from vanilla RAG, and point me at further reading. | QA **and** a recommender, one turn | ✅ `answer_question` → `recommend_for_query` |
| 6 | Who won the 2019 Cricket World Cup? | no function call | ✅ *(no function call)* |

**6 / 6, with zero string matching anywhere in the codebase** — routing is decided entirely by
`FunctionChoiceBehavior.Auto()` reading `[Description]` text.

**Utterance 4 — the one the brief says will misroute — did misroute on the first attempt**, to
`answer_question`: a reading request with a named topic is syntactically indistinguishable from a
factual question about it. The fix was in the descriptions, not a regex and not the system prompt:
a **negative boundary** on `answer_question` ("Do NOT use this when the user asks what they should
read about a topic… use `recommend_for_query` instead") plus a **positive boundary** on
`recommend_for_query` claiming that territory. Naming the competing function in both directions
settled it — describing *when to use* a function beat describing *what it does*, as §16 predicts.

**Utterance 6** requires declining *without searching*, which means the instructions must state
the library's scope so the model can judge before reaching for a tool. Scope wording is a
two-sided error: too loose and it searches for cricket; too tight and real questions get refused —
an earlier version declined the in-scope RAG-Sequence formula question, fixed by adding that
methods, formulas and findings *within* those areas are in scope.

## 10. Limitations and what I would do next

1. **The QA path has no lineage awareness**, unlike the recommender — a `docId`-level rerank hint
   preferring the newest member of a lineage group would likely fix qa14 alone.
2. **The 8-seed recommender set is small.** A 0.017 nDCG difference (λ=1.0 vs 0.7) is not
   resolvable at that sample size, and I have not claimed it is.
3. **LLM-judge metrics are not deterministic** — Faithfulness moved 1.000 → 0.975 between two runs
   of an identical configuration. Read judge metrics as ±1 case.
4. **Context Precision@6 is budget-bound, not quality-bound** at topK=6 where most questions have
   one or two relevant chunks; precision@2 would discriminate better.
