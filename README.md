# Athena — Grounded PDF RAG + Document Recommender

Semantic Kernel for .NET assignment: one ingestion pipeline, one Kernel, two retrieval regimes
(chunk-granularity QA and document-granularity recommendation), routed by the model via
`FunctionChoiceBehavior.Auto()`.

## Setup

**Prerequisites:** .NET 8 SDK, an OpenAI API key, and an Azure AI Document Intelligence resource
(replaces PdfPig + Tesseract — see Deviations §1).

Secrets are user-secrets only (never in the repo). `Athena.Web`, `Athena.Ingestion` and
`Athena.Eval` share **one** user-secrets id, so a single set of commands serves all three:

```
dotnet user-secrets set "OpenAI:ApiKey"                        "sk-..."                  --project src/Athena.Web
dotnet user-secrets set "OpenAI:ChatModelId"                   "gpt-4o-mini"             --project src/Athena.Web
dotnet user-secrets set "OpenAI:EmbeddingModel"                "text-embedding-3-small"  --project src/Athena.Web
dotnet user-secrets set "AzureDocumentIntelligence:Endpoint"   "https://<res>.cognitiveservices.azure.com/" --project src/Athena.Web
dotnet user-secrets set "AzureDocumentIntelligence:ApiKey"     "..."                     --project src/Athena.Web
```

Optional — enables the demo's estimated-cost figure (list prices in USD per million tokens for
the chat model; the metrics strip shows `n/a` rather than a misleading `$0.00` when unset):

```
dotnet user-secrets set "OpenAI:Pricing:InputUsdPerMillionTokens"  "0.15" --project src/Athena.Web
dotnet user-secrets set "OpenAI:Pricing:OutputUsdPerMillionTokens" "0.60" --project src/Athena.Web
```

Then, from the repository root:

```
dotnet build                                                  # clean build, all projects
dotnet run --project src/Athena.Ingestion -- fetch            # download the 20 corpus PDFs (never committed)
dotnet run --project src/Athena.Ingestion -- rasterise        # manufacture corpus/A1-scanned.pdf (200 DPI, pages 1-12)
dotnet run --project src/Athena.Ingestion -- index            # ingest: extract -> chunk -> summarise -> embed
                                                              #   defaults to the chosen config (--chunker section --docvec centroid);
                                                              #   pass --chunker fixed / --docvec composite for the other ablation arms
dotnet run --project src/Athena.Web                           # Blazor Server demo
```

Evaluation (writes CSV to `eval/results/`):

```
dotnet run --project src/Athena.Eval -- qa                    # Part F QA metrics, 25-case gold set
dotnet run --project src/Athena.Eval -- rec                   # Part F recommender metrics, 8 seeds
dotnet run --project src/Athena.Eval -- ablation retriever    # ablation 1
dotnet run --project src/Athena.Eval -- ablation docvec       # ablation 2
dotnet run --project src/Athena.Eval -- ablation lambda       # ablation 3
dotnet run --project src/Athena.Eval -- ablation dedup        # ablation 4
dotnet run --project src/Athena.Eval -- chunker-ablation      # chunker choice (design decision 1)
dotnet run --project src/Athena.Eval -- lambda-sweep          # §9.1 list for seed B3 at lambda 1.0/0.7/0.3
dotnet run --project src/Athena.Eval -- routing               # Part E: six utterances -> resolved calls
```

**Web app note.** The demo does *not* auto-ingest at startup. Open the **Corpus** tab, select
documents, and press *Ingest selected*; the chat's status chip shows how many documents are
indexed. Vectors are in-memory, so re-ingest after a restart — with warm caches this is seconds
(see Deviations §4.7). Selective ingestion exists so a demo can run on two documents without
paying for twenty.

## Architecture

```
PDFs -> Athena.Ingestion ---> chunk collection (1536-d vectors)           \
        (one cached DI call    doc collection   (21 records, centroid)    |  one Kernel,
         per document)                                                    |  one embedding
                                                                          |  generator
Athena.Retrieval:  dense top-20 + BM25 top-20 -> RRF(k=60) -> top-10      |
                   -> LLM rerank -> per-document cap -> top-6            /
Athena.Recommendation: doc-vector similarity + chunk-hit aggregation + recency
                   -> lineage dedup -> MMR(lambda=0.7) -> signal-grounded reasons

Athena.Plugins (thin adapters):  SearchPlugin { hybrid_search, answer_question, read_pages }
                                 RecommendPlugin { more_like_this, recommend_for_query,
                                                   recommend_for_user }
Athena.Agent:   ChatCompletionAgent + FunctionChoiceBehavior.Auto()   <- all routing lives here
Athena.Filters: TelemetryFilter | PiiRedactionFilter | GroundingGuardFilter
Athena.Web:     Blazor Server (streaming chat, citation panel, recommendation sidebar, metrics)
Athena.Eval:    console harness -> eval/results/*.csv
```

Two retrieval regimes, one pipeline: QA runs at **chunk** granularity and is precision-oriented;
recommendation runs at **document** granularity and is diversity-oriented. Nothing in the plugin
layer decides which regime applies — the model does, from `[Description]` text alone.

## Design decisions

Every decision below is stated with its cost, as the brief requires. Numbers come from
`eval/results/` (see REPORT.md for the full tables and interpretation).

**1. Chunker: section-aware (split on DI headings, subdivide >1,000 tokens), not fixed-window.**
Measured, and the measurement *changed my mind*. Against an interim 17-case gold set the two
chunkers tied on Context Recall@6 and fixed windows scored marginally better precision, so fixed
was the working default. Re-running against the final 25-case set — which adds five cluster-D
questions drawn from 32-to-80-page NIST/CISA documents — reversed it decisively
(`20260801-064006-ablation-chunker.csv`):

| Metric | fixed | section |
|---|---|---|
| Context Recall@6 | 0.750 | **0.900** |
| Context Precision@6 | 0.192 | **0.208** |
| Answer Correctness | **0.975** | 0.900 |

Recall +0.150 is three more questions in twenty whose evidence is found at all, and it is
deterministic set arithmetic rather than a judged score. The mechanism is visible in the two
gold sets: short regulatory documents (the BCBS principles) have few `sectionHeading` labels and
gain nothing from section splitting, so a 17-case set weighted toward them saw no difference.
Long standards documents have strong heading structure, and a fixed window that straddles two
sections dilutes both — which is precisely the failure mode behind the D3 and D5 recall misses
reported in REPORT.md §2.

*Costs, stated plainly.* Two, and the second is the interesting one:

1. Answer Correctness moves the other way in the ablation (0.975 → 0.900) — 1.5 cases of 20 on a
   non-deterministic LLM judge whose run-to-run variance I measured at the same magnitude, against
   a 3-case deterministic recall gain. I weight recall higher without claiming the drop is noise.
2. **The recommender gets worse.** Measured on the shipped configuration, section chunking costs
   **0.054 nDCG@5** (0.803 vs 0.857) and 0.024 intra-list diversity, because document vectors are
   centroids of chunk embeddings — changing chunk boundaries changes every document vector. QA and
   recommendation are two regimes on one pipeline, and this upstream knob moves them in *opposite*
   directions. I chose QA recall because a wrong answer is worse than a mediocre reading list;
   REPORT.md §3 reports both columns side by side rather than only the flattering one.

Section chunks are also more variable in length, making per-chunk cost less predictable, and the
strategy inherits whatever DI's `sectionHeading` labelling gets wrong. `FixedWindowChunker`
remains available via `chunker=fixed` and is the arm every ablation holds constant (REPORT.md §1).

**2. Fusion: Reciprocal Rank Fusion, k=60 — never a score blend.** Cosine similarity is bounded
in [0,1]; Lucene BM25 is unbounded and scales with corpus statistics, so their raw sum is
meaningless and min-max normalising them makes the ordering depend on whichever outlier happens
to be in the batch. RRF discards magnitudes and fuses *ranks*: `score(d) = Σ 1/(k + rank_i(d))`.
k=60 is the Cormack et al. (2009) constant and is deliberately large relative to the top ranks —
it damps the gap between rank 1 and rank 5 so a single retriever cannot dominate the fused list,
and consensus between the two retrievers outranks one retriever's confident singleton. *Cost:*
genuine confidence information is thrown away — a dense hit at 0.95 and one at 0.55 fuse
identically if they sit at the same rank.

**3. Document vector: centroid, not composite.** Measured: centroid scores nDCG@5 0.857 / MRR
1.000 against composite's 0.768 / 0.938, while composite buys higher Intra-List Diversity (0.342
vs 0.240) and catalogue coverage (0.900 vs 0.750). *Declared substitution:* the brief's ablation
2 names "centroid vs summary-embedding"; the two strategies implemented here are `Centroid` and
`Composite` (`Title + Topics + Summary`), so the second arm is summary-**plus-metadata** rather
than summary alone — §6.4 asks for at least two strategies and these are the two built. This *contradicts* the brief's prediction that a centroid turns the
survey (B3) into a blob near the corpus centre — and the reason is instructive: the
composite vector is built from a ≤150-word LLM summary of the document's leading ~6k tokens
(Deviations §4.6), so for long documents it inherits a *truncation* loss that is worse than the
centroid's *averaging* loss. Centroid also keeps late-document content represented at all.
*Cost:* the centroid genuinely is a poor descriptor of a heterogeneous document; B3 sits closer
to the corpus mean than any other document, and MMR plus the per-document cap are what stop it
from crowding every list.

**4. Near-duplicate resolution: computed lineage groups, newest member surfaces.** `LineageGroup`
is assigned at ingestion by `LineageDetector` from four signals that must *all* hold — same
cluster, publication dates within 730 days, title Jaccard ≥ 0.55 after version-marker
normalisation, and doc-vector cosine > 0.95 — then transitively closed. Within a group only the
newest member surfaces; suppressed siblings are carried as provenance so the reason line can say
"Supersedes d509, Aug 2020". Ungrouped documents still get a cosine ceiling of 0.95 as a
fallback. *Cost (named):* this is the most accurate and least general of the brief's three
options — it works because these documents really are versions of each other, and it depends on
lineage detection being right; a false link silently hides a genuinely distinct document.
*If a fourth lineage pair were added tomorrow:* nothing changes in code. The four gates are
generic (no filenames, no doc-id lists — hard constraint 2), so the new pair is detected at the
next ingestion; the corpus-shaped unit test asserts exactly the three engineered pairs are found
and would need one line updated to expect four.

**5. Recency prior: tau = 1000 days, with a floor of 0.3.** `recency = 0.3 + 0.7·exp(−ageDays/1000)`.
Tau ≈ 2.7 years is set by the corpus's actual spread, not by intuition: A1/A2 are 237 days apart,
so the draft scores 0.85 against the final's 1.0 with the floor applied (0.79 unfloored) — enough
to prefer the final without swamping topical signal — while B1 (2021) and C3 (2025) are 1,367
days apart (0.48 floored, 0.26 unfloored). *Deviation from the brief's bare
`exp(−age/tau)`:* the 0.3 floor exists because this corpus contains standards, not news — NIST
SP 800-61r2 (2012) is thirteen years old and still the current guidance, and an unfloored
exponential would score it ~0.006, effectively deleting a relevant document for being old.
*Cost:* the floor compresses the top of the recency range, so recency discriminates less among
recent documents than a bare exponential would.

**6. Interest profile: decay 0.8, but affinity scored by max-over-recent-queries.** The brief's
question — an analyst spends twenty turns on cluster A, then switches to cluster C. With
decay = 0.8 the old profile's weight after *n* new turns is 0.8ⁿ: the half-life is
ln(0.5)/ln(0.8) ≈ **3.1 turns**, it takes 11 turns to fall below 10% (0.8¹⁰ = 0.107, 0.8¹¹ =
0.086) and 14 to fall below 5%.
So for roughly turns 2–8 the decayed mean is an exponentially-weighted average of two unrelated
topics — a vector that points *between* the clusters and is therefore about neither, exactly as
the brief suspects. **What we did about it:** `recommend_for_user` does not score against the
decayed mean. `ProfileAffinity` scores each candidate by its **maximum** cosine over the last
five *raw* query vectors, so a cluster-C candidate is judged against the actual cluster-C
queries and a cluster-A candidate against the A ones; both interests stay recommendable through
the crossover instead of neither. The decayed mean is still maintained and is used as the MMR
seed. *Cost:* the profile stops expressing a single coherent interest — a genuinely blended
interest ("regulatory *and* RAG") is served as alternating single-topic picks rather than
documents that sit between the two, and a stale query from five turns ago retains full voting
power until it leaves the window.

**7. Grounding guard: strip-and-warn, not fail-the-turn.** Unsupported `[Title, p.N]` tags are
removed from the answer, an explicit warning naming what was removed is appended, and every
violation is logged to `logs/citation-violations.jsonl`. Rationale: a hallucinated citation is
usually one sentence in an otherwise-supported answer, and failing the whole turn throws away
the correct, properly-cited content along with it — the user loses more than they gain. Stripping
keeps the supported material, makes the removal visible rather than silent, and preserves the
audit trail the violation-rate metric reads. *Cost:* the stripped sentence remains in the answer
without attribution, so a careless reader sees an uncited claim rather than no claim; in a
compliance setting (where an unsupported assertion is worse than no answer) hard-fail is the
defensible choice, and the filter is one `return` away from it.

**7b. Recommendation weights w1/w2/w3 = 0.5 / 0.3 / 0.2, and the length-bias normalisation.**
`final = 0.5·docSim + 0.3·normalise(chunkAggregate) + 0.2·recency`. Doc similarity carries the
most weight because it is the only signal defined for *every* candidate on *every* query; chunk
aggregation is a stronger discriminator when it fires but is zero for most documents on a focused
query; recency is a nudge and must never outrank topic. The brief permits fixed weights but asks
them to be defended — the honest position is that these were reasoned, not tuned: **there is no
weight ablation in Part F**, so I can defend the ordering (docSim > chunkAgg > recency) but not
the exact values, and a weight sweep is the first experiment I would add.

Chunk-hit aggregation handles the length bias the brief warns about (§9.3) in three ways, all in
`RecommendationScorer`: hits are **rank-weighted** `1/(60+rank)` so four strong hits beat one
lucky chunk; capped at **5 hits per document** so a long document cannot win by volume; and
divided by **√chunkCount**, which discounts long documents without erasing them (a linear
`/chunkCount` over-corrected and buried B3 entirely). *Cost:* √ is a judgement call between two
wrong extremes, and a document whose relevant material is genuinely spread over many chunks is
still penalised for its length.

**8. MMR lambda = 0.7 — a deliberate trade, not the metric maximum.** Measured across
{1.0, 0.7, 0.3}: nDCG@5 0.874 / 0.857 / 0.526 against Intra-List Diversity 0.230 / 0.240 / 0.541.
λ=1.0 is nominally the best-scoring arm, and λ=0.3 collapses relevance entirely. 0.7 is chosen
because it costs 0.017 nDCG — inside the noise of an 8-seed gold set — for a list that is not
simply "the five documents nearest the seed", which is what a recommender is *for*. *Cost:*
stated plainly, this choice is not what the gold set rewards; nDCG measures agreement with a
relevance label, and it has no way to credit a list for being useful rather than repetitive.

---

## Deviations from the brief

The brief invites departures from its prescribed structure provided they are declared and
justified here. This section is the running record; it is pre-seeded from the Sprint 0
API-surface spike (2026-07-29) and grows as later sprints touch the affected methods.

### 1. Extraction: Azure AI Document Intelligence replaces PdfPig + Tesseract

**What changed.** The brief prescribes PdfPig for text/layout and Tesseract (behind a
Docnet.Core rasteriser) for OCR, routed per page at a <50-character threshold. This build uses
**Azure AI Document Intelligence** (`prebuilt-layout`) as the single extractor for text, tables,
OCR, and layout roles, in one call per document.

**Why.** One engine gives uniform text, structured tables (rows/columns/spans rather than
whitespace geometry), OCR without a local Tesseract install, per-word confidence scores, and
paragraph roles (title/sectionHeading) with reading order — each of which the brief otherwise
has us hand-roll. The brief's interfaces (`IPdfTextExtractor`, `ITableExtractor`) are kept as
the seam, so everything above extraction is unaware of the engine, and a PdfPig fallback would
be a swap rather than a rewrite.

**Method-level consequences, each with its cost:**

| Brief's method | This build | Cost / note |
|---|---|---|
| PdfPig text extraction | DI `prebuilt-layout` text | Paid, per-page, network service (mitigated by the response cache below) |
| Tesseract OCR fallback | DI OCR, built into the same pass | No second engine to compare against; the OCR Delta metric becomes DI-image-OCR vs DI-text-layer |
| Per-page *routing* (<50 chars → OCR engine) | Per-page *classification*: each page is classified text-layer-derived vs image-OCR-derived from DI's per-word confidence, and image-OCR pages get `ChunkKind = OcrProse` | The intent (per-page OCR awareness feeding `PagesOcrd` and the OCR Delta) is preserved; the mechanism is a classifier, not a router. This is the most-scrutinised deviation — see also finding 3.4 below: DI exposes confidence per **word**, not per page, so the page's `MeanConfidence` is computed by aggregating word confidences |
| Hand-rolled table detection from bounding boxes | DI structured tables serialised to Markdown | Less control over merged/borderless cells; any DI mangling is logged and reported rather than hand-fixed |
| Bounding-box section/column detection | DI paragraph roles + reading order | Trusting DI's role labels; spot-checked across clusters |
| Docnet.Core rasterisation | **Kept unchanged** | Still manufactures the scanned test PDF (`A1-SCAN`); it is not an extractor |
| — | **New:** DI response cache, `corpus/.di-cache/{sha256}.json`, one `prebuilt-layout` call per document ever | Keeps re-index under two minutes (cache, not network) and controls cost; cache is gitignored |
| — | **New:** `AzureDocumentIntelligence:Endpoint` / `:ApiKey` in user-secrets | Ingestion needs network + an Azure resource |

### 2. Package versions vs the brief's "SK 1.6x / VectorData 9.x" line

Pinned in `Directory.Packages.props`; the brief's remembered versions have moved on:

| Package | Brief assumes | Installed | Why |
|---|---|---|---|
| Microsoft.SemanticKernel (+ Connectors.OpenAI, Agents.Core) | 1.6x | **1.78.0** | Current stable line |
| Microsoft.Extensions.VectorData.Abstractions | 9.x | **10.1.0** (pinned exactly) | Pinned to the version the 1.74.0-preview InMemory connector was compiled against. 10.8.0 removed `VectorSearchFilter` — an in-major break during the preview era — and the connector still references it, so anything newer throws `TypeLoadException` on the first `SearchAsync` **at runtime**, not at build. Found live; see FAILURES below |
| Microsoft.Extensions.AI.Abstractions | — | **10.8.0** | Separate package, separate line — this is where `IEmbeddingGenerator<string, Embedding<float>>` lives |
| Microsoft.SemanticKernel.Connectors.InMemory | (stable implied) | **1.74.0-preview** | No stable release of this connector exists on NuGet |
| Lucene.Net | (stable implied) | **4.8.0-beta00018** | The 4.8 line — the only maintained codebase — ships beta-only; the nominally stable 3.0.3 dates from 2012 |
| Azure.AI.DocumentIntelligence | — (not in brief) | **1.0.0** GA | Service API version 2024-11-30 |

### 3. API-surface spike findings (verified by reflection against the installed assemblies)

The spike loaded the restored assemblies and dumped the actual types/members; nothing below is
from memory.

**3.1 SK / vector-data surface — matches the brief's naming.** All of the following exist
exactly as the brief names them, in VectorData.Abstractions 10.8.0 / SK 1.78.0:
`[VectorStoreKey]`, `[VectorStoreData]` (with `IsIndexed`, `IsFullTextIndexed`),
`[VectorStoreVector(int dimensions)]`, `VectorStoreCollection<TKey,TRecord>` (an abstract class;
the older `IVectorStoreRecordCollection` interface no longer exists),
`IEmbeddingGenerator<string, Embedding<float>>` (from Microsoft.Extensions.AI.Abstractions),
`IFunctionInvocationFilter` / `IPromptRenderFilter` / `IAutoFunctionInvocationFilter`,
`ChatCompletionAgent` + `ChatHistoryAgentThread`, and
`FunctionChoiceBehavior.Auto(functions = null, autoInvoke = true, options = null)`.

**3.2 One nuance:** `VectorStoreVectorAttribute.DistanceFunction` is a **string** property
(assigned from the `DistanceFunction` string-constants class, e.g.
`DistanceFunction = DistanceFunction.CosineSimilarity`), not an enum. The brief's record
skeleton compiles as written; only the mental model of the type differs.

**3.3 Azure DI client surface (Azure.AI.DocumentIntelligence 1.0.0).**

- Client: `DocumentIntelligenceClient(Uri endpoint, AzureKeyCredential credential)`.
- Analyze: `AnalyzeDocumentAsync(WaitUntil waitUntil, AnalyzeDocumentOptions options, CancellationToken ct)`
  returning `Operation<AnalyzeResult>` — a long-running operation, polled async
  (`WaitUntil.Started` + `WaitForCompletionAsync(ct)`).
- Options: `new AnalyzeDocumentOptions("prebuilt-layout", BinaryData bytesSource)` (or
  `Uri uriSource`). **There is no stream/file-path overload** — local PDFs go in as
  `BinaryData` from bytes.
- Result: `AnalyzeResult.Pages` → `DocumentPage { PageNumber, Words, Lines, Spans, ... }`;
  `DocumentWord { Content, Confidence, Span, Polygon }`;
  `AnalyzeResult.Paragraphs` → `DocumentParagraph { Role?, Content, BoundingRegions }` with
  `ParagraphRole` values including `Title`, `SectionHeading`, `PageHeader`, `PageFooter`,
  `PageNumber`, `Footnote`;
  `AnalyzeResult.Tables` → `DocumentTable { RowCount, ColumnCount, Cells, BoundingRegions, Caption }`
  with `DocumentTableCell { RowIndex, ColumnIndex, RowSpan?, ColumnSpan?, Content, Kind }`
  (`Kind`: Content / RowHeader / ColumnHeader / StubHead / Description).

**3.4 Confidence is word-level, not page-level, and there is no "text layer present" flag.**
`DocumentWord.Confidence` exists; `DocumentLine` and `DocumentPage` carry no confidence, and
the result nowhere states whether a page had an embedded text layer. Consequences for the
brief's shapes: `PageText.MeanConfidence` is **computed** (mean of word confidences on the
page), and the per-page OCR classification of deviation 1 is a heuristic over that word-level
confidence distribution (threshold to be chosen and defended when the extractor lands),
not a metadata read-off.

**3.5 `ITableExtractor` is async in this build.** The brief's skeleton declares a synchronous
`IReadOnlyList<(int PageNumber, string MarkdownTable)> Extract(string pdfPath)`. Here tables are
read from the same cached DI analysis as the text (one `prebuilt-layout` call per document,
rule 11), which is disk I/O on every run and a network call on the first — so the seam becomes
`Task<IReadOnlyList<PageTable>> ExtractAsync(string pdfPath, CancellationToken ct = default)`,
with `PageTable` a named record struct replacing the anonymous tuple. Span handling: Markdown
has no rowspan/colspan, so a spanning cell's content is repeated into every covered cell —
rows and columns stay truthfully aligned for retrieval at the cost of some duplicated text.
Degenerate DI detections (no rows/columns, or only empty cells) are logged as warnings and
skipped rather than serialised as garbage.

**3.6 Tooling note.** `dotnet new sln` under the .NET 10 SDK emits the new `.slnx` format;
the repo uses the classic `Athena.sln` the brief's layout names, generated with
`--format sln`.

### 4. Sprint 2 deviations (chunking, records, doc vectors, lineage)

**4.1 `IChunker` takes an `ExtractedDocument`, not `(meta, pages, tables)`.** The brief's §6.3
signature predates two IDP-driven inputs: DI **paragraph roles** (section detection uses
`title`/`sectionHeading` labels instead of the PdfPig bounding-box heuristics the brief
assumed, via a new `IParagraphExtractor` seam over the same cached analysis) and the per-page
**OCR classification** (rule 9), which must reach each chunk so pages classified as
image-OCR-derived yield `ChunkKind.OcrProse` chunks. Both travel in one
`ExtractedDocument(Meta, Pages, Paragraphs, Tables, PageKinds)` input. Cost: a slightly wider
chunker input than the brief's; chunkers remain pure functions.

**4.2 `ChunkRecord.Kind` behaviour.** A chunk inherits the OCR classification of the page it
*starts* on (chunks may cross page boundaries; `PageNumber` follows the same start-page
convention). Table text also remains inside prose chunks (DI's line stream includes table
content); tables are additionally emitted as their own unsplit `Kind=Table` chunks. The
duplication is accepted — deduplicating table regions out of the line stream costs geometry
work for little retrieval gain, and reranking tolerates it.

**4.3 `Athena.Core` references `Microsoft.Extensions.VectorData.Abstractions`.** Hard
constraint 5 bars SK and Azure packages from Core; the vector-store attributes the brief's own
`ChunkRecord` skeleton uses live in this Microsoft-Extensions abstraction package (neither SK
nor Azure), so Core takes exactly that one reference.

**4.4 `manifest.json` gained `publishedOn`.** Lineage detection and recency scoring need
dates; the brief's manifest had none. Dates are the publication date of the revision each URL
actually serves, verified against BIS pages and arXiv submission histories (versionless arXiv
URLs serve the *latest* revision — B1 carries its v4 date). Measured consequence: the
engineered C4/C5 pair is ~580 days apart, so the lineage date window is 730 days, not the
~8-month BCBS cycle a first guess would suggest.

**4.5 Title similarity uses version-marker normalisation.** Raw token Jaccard scores the A1/A2
draft/final pair *lower* (0.36) than the A1/A3 non-pair (0.41): editions differ in exactly the
tokens (status words, dates, document codes) that same-publisher non-pairs share. Titles are
therefore compared after stripping version markers — status words ("final", "consultative
draft"), month/year tokens, version tags ("v1"), and short letter+digit codes ("d516") — a
generic rule, not a corpus-specific list; a fourth pair added tomorrow needs no new code. Cost:
a hypothetical corpus where the *only* distinguishing title token is a document code would
over-merge; the cosine and date gates still have to refuse those links.

**4.6 LLM summaries are truncated to ~6k input tokens.** `SkDocumentSummariser` feeds the
document's leading ~6,000 tokens to the prompt. For C3 (88 pages) that skips most of the body;
title/abstract/introduction carry the thesis, and a ≤150-word summary cannot represent 88 pages
regardless. Cost: composite doc vectors under-represent late-document content — acknowledged in
the §6.4 strategy comparison.

**4.7 Two more content-hash caches beside the DI cache.** `corpus/.enrichment-cache/`
(summaries+topics) and `corpus/.embedding-cache/` (one JSON per distinct text, keyed by
model+text hash). The DI cache alone does not keep re-index under the brief's two minutes — a
cold run makes thousands of embedding calls and 16 summary calls; with all three caches warm, a
re-index touches no network at all. Both directories are gitignored.

**4.8 SKEXP0010 suppression.** SK 1.78 marks `AddOpenAIEmbeddingGenerator` experimental even
though it replaces the *obsoleted* `AddOpenAITextEmbeddingGeneration`; the composition root
suppresses the diagnostic at the single call site (API drift between the SK stable line and its
Microsoft.Extensions.AI migration).

### 5. Later deviations (retrieval, plugin surface, extraction, UI)

Each of these was driven by an observed failure, not by preference. They are listed with what
was seen, because the failure is the justification.

**5.1 DI *formulas* add-on, and LaTeX spliced back into page text.** `prebuilt-layout` alone
linearises typeset mathematics into lookalike ASCII: B1's RAG-Sequence equation came out with
`p_η`/`p_θ` rendered as `P_n`/`P_o`, and every answer quoting it inherited the corruption while
remaining perfectly "grounded" in the corrupted chunk. The analyzer now requests
`DocumentAnalysisFeature.Formulas`, which returns each equation's LaTeX; `FormulaRestorer`
splices it back into the `:formula:` placeholders DI leaves in the text, matched by span
containment. *Costs:* the add-on is a paid extra on top of per-page DI pricing, and the cache key
gained a `-formulas` suffix so pre-add-on cached analyses are not silently reused — meaning each
document pays for exactly one more DI call, once, ever. Placeholders with no LaTeX are left in
place rather than deleted, so a failed restoration is visible instead of invisible.

**5.2 New kernel function `read_pages` (a seventh function, beyond the brief's six).** A question
naming a *location* ("how many references are on page 16 of B1") cannot be served by similarity
retrieval: a page number has no meaning in embedding space and almost none in BM25, so the
retriever returned nothing usable and the agent correctly abstained. `read_pages` is
location-addressed rather than meaning-addressed — it selects chunks by `DocId` and page span
from the store the ingestion already produced (`PageReader`, capped at 5 pages so a "pages 1–12"
ask cannot dump a document into the prompt). Routing between it and `hybrid_search` is, as
everywhere else, decided by `[Description]` text alone. *Cost:* a seventh function is one more
opportunity for the model to misroute, which is why its description names the negative boundary
explicitly ("do NOT use for topical questions where no page is named").

**5.3 `DocIdResolver`: user-facing identifiers resolved to DocIds.** `hybrid_search(docId:)` is a
hard filter, and users name documents the way the documents name themselves. Asked "what does
d516 say about tolerance for disruption", the model passed `docId: "d516"` — the catalog id is
`A1`, "d516" appears only in the title — the filter matched zero chunks, and the answer said
d516 contains no information about its own subject. Both `hybrid_search` and `read_pages` now
resolve the argument against the doc catalog (exact id/title, then *unique* title substring)
before it becomes a filter; ambiguous fragments return the candidate list instead of guessing,
and unresolved ones return an explicit error rather than a silent zero-match. This is
argument→catalog entity resolution *inside* an already-chosen function — routing remains
entirely with `FunctionChoiceBehavior.Auto()` (hard constraint 1).

**5.4 Per-document diversity cap in QA retrieval (max 4 of 6 passages per document).** Asked why
the RAG authors chose DPR over BM25 — a question spanning B1 (which made the choice) and B2
(which describes DPR's properties) — the top-6 was entirely B2, because B2 is where the words
"DPR" and "BM25" are dense. The answer addressed the wrong paper. The reranker now orders the
full 10-passage shortlist and `DiversityCap` selects the final 6 with at most 4 from any one
document, backfilling if fewer documents match so K never shrinks; `docId`-filtered searches skip
the cap, being single-document by construction. The agent instructions additionally ask for one
focused search *per side* of a comparative question. *Cost:* a genuinely single-document question
gives up its 5th- and 6th-best passages to make room for a runner-up document.

**5.5 Docnet's role widened from manufacture-only to a text-layer probe.** Deviation 1 promised a
confidence-threshold classifier for per-page OCR awareness. Measured on the real corpus, that is
undecidable: A1-SCAN (a clean 200-DPI raster) averaged word confidence **0.9913** against native
A1's **0.9902**, with indistinguishable tails and 4,071 of 4,074 words recovered. DI simply does
not expose whether a page had a text layer. `DocnetTextLayerProbe` therefore reads each page's
embedded text *only to measure its length* (<50 non-whitespace characters ⇒ no text layer) — the
brief's own §6.2 signal, and the one deterministic discriminator available. The text is measured
and discarded: never stored, chunked, or embedded, so extraction remains Azure DI end to end.
Confidence is kept as a secondary gate (< 0.90). *Cost:* Docnet is now a (read-only) participant
in ingestion rather than purely a fixture generator.

**5.6 `SearchPlugin` constructor takes three optional collaborators.** The brief's §7.1 ctor is
`SearchPlugin(HybridRetriever)`. This build adds `IRetrievedContextAccessor?` (how the §8.1
grounding guard learns what was retrieved this turn without the plugin knowing the guard
exists), `PageReader?` (5.2), and the doc catalog (5.3). All are optional, so the brief's
one-argument construction still compiles and is what the unit tests use.

**5.7 Web app: on-demand ingestion, not startup ingestion.** The Blazor app starts with empty
collections and a **Corpus** page that fetches/ingests only the documents you select, reporting
per-document chunk counts. Startup ingestion made every launch pay the full pipeline before the
first question could be asked. Partial ingestion is additive and lineage is recomputed over the
union of the batch and what is already in the store, so ingesting A1 today and A2 tomorrow still
lands them in one lineage group. *Cost:* vectors are in-memory, so a restart requires
re-ingesting (seconds with warm caches) — and a demo that forgets to ingest gets an empty
library, which the chat's status chip and empty-state hint exist to prevent.

**5.7b Record and interface additions to the brief's skeletons.** Collected here because several
source files reference "the README" for them:

| Brief skeleton | Addition | Why / cost |
|---|---|---|
| `Passage` (§7) | `int EndPage` | An ~800-token chunk spans ~2 pages, so a cited page may sit after the chunk's start page; the grounding guard validates against the `[PageNumber, EndPage]` span rather than the start page alone. Without it, legitimate citations were being stripped |
| `IInterestProfileStore` (§9.4) | `MarkSurfacedAsync`, `GetSnapshotAsync`, `InterestSnapshot` | The brief's `GetAlreadySurfacedAsync` reads the surfaced set but nothing writes it; and three separate awaits (profile, recent queries, surfaced) race against a concurrently-updating session, so one snapshot read replaces them |
| `INearDuplicateResolver` (§9.2) | `ResolveWithProvenance` → `ResolvedDoc(Doc, SuppressedSiblings)` | §9.5 requires reasons grounded in real signals; "Supersedes d509, Aug 2020" is only sayable if the resolver reports *what* it suppressed. The bare `Resolve` overload is kept and both are tested for agreement |
| `RecommendPlugin` ctor (§9.6) | `+ HybridRetriever`, `+ IEmbeddingGenerator`, `+ TimeProvider`, `+ string sessionId` | `recommend_for_query` cannot exist without a query embedder and chunk retrieval; recency needs a clock (pure functions take the clock as input, never read it); the scoped profile store needs this session's id. None is inventable from the brief's five parameters |
| `IngestionPipeline` ctor (§6.5) | `+ IParagraphExtractor`, `+ ITextLayerProbe` | Section detection reads DI paragraph roles (§4.1) and per-page OCR classification needs the text-layer probe (§5.5) |

**5.8 KaTeX in the citation/answer surface.** Answers that quote restored LaTeX (5.1) would
otherwise render as raw `\[ … \]` markup. Completed assistant messages are typeset client-side.
*Cost:* KaTeX loads from a CDN, so a fully offline demo needs those three files vendored into
`wwwroot/`.

---

## FAILURES worth knowing (kept because they cost real time)

- **VectorData 10.8.0 + InMemory 1.74.0-preview compiles and then throws.** `TypeLoadException`
  on the first `SearchAsync`, because the connector references `VectorSearchFilter`, removed in
  10.8.0. Diagnosis is only possible at runtime. Pin VectorData.Abstractions to **10.1.0**.
- **DI word confidence cannot detect a scanned page** (5.5) — 0.9913 vs 0.9902.
- **A "grounded" answer can still be wrong** if extraction corrupted the chunk (5.1). Grounding
  guarantees faithfulness to the retrieved text, not to the source PDF.
- **A zero-match filter is indistinguishable from an empty corpus** to the model (5.3). Filters
  built from user-supplied identifiers must fail loudly.
