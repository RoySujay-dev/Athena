# Corpus sources

Every corpus file, its origin, retrieval date, and licence (brief §4). PDFs are **not**
committed; `dotnet run --project src/Athena.Ingestion -- fetch` downloads them from the URLs
below into `corpus/`, driven by `manifest.json`. If a URL moves, find the document by title on
the publisher's site and record the substitution here.

> **Note on page counts (2026-07-29).** The versionless arXiv URLs serve each paper's *latest*
> revision, so actual page counts differ from the brief's estimates (e.g. C3 is now 88 pp, C1 is
> 41 pp); titles were spot-checked to confirm the documents are the ones prescribed. The BIS
> PDFs as currently served are also shorter than the brief's estimates (A1 is 12 pp, not ~20) —
> which conveniently means the brief's "rasterise pages 1–12 of A1" covers the whole document.
> `expectedPages` in `manifest.json` records the brief's estimate, not a validation constraint.

> **Note on `publishedOn` (2026-07-29).** Each manifest entry carries the publication date of the
> revision the URL actually serves, verified against BIS publication pages and arXiv submission
> histories on 2026-07-29 (versionless arXiv URLs serve the *latest* revision, so e.g. B1 carries
> its v4 date, 2021-04-12, not its v1 date). Lineage detection and recency scoring read this
> field. Measured curiosity: the engineered C4/C5 pair (RAGAS v1 vs v2) is ~580 days apart —
> arXiv revisions can be years apart, which sets the floor for any lineage date window.

## Cluster A — Operational resilience regulation (BIS / Basel Committee)

| Id | Title | URL | Retrieved | Licence | Cluster |
|----|-------|-----|-----------|---------|---------|
| A1 | Principles for Operational Resilience (final, Mar 2021) — BCBS d516 | https://www.bis.org/bcbs/publ/d516.pdf | 2026-07-29 | © BIS; reproduction permitted with attribution | A |
| A2 | Principles for Operational Resilience (consultative draft, Aug 2020) — BCBS d509 *(pairs with A1)* | https://www.bis.org/bcbs/publ/d509.pdf | 2026-07-29 | © BIS; reproduction permitted with attribution | A |
| A3 | Revisions to the Principles for the Sound Management of Operational Risk (final, Mar 2021) — BCBS d515 | https://www.bis.org/bcbs/publ/d515.pdf | 2026-07-29 | © BIS; reproduction permitted with attribution | A |
| A4 | Revisions to the PSMOR (consultative draft, Aug 2020) — BCBS d508 *(pairs with A3)* | https://www.bis.org/bcbs/publ/d508.pdf | 2026-07-29 | © BIS; reproduction permitted with attribution | A |
| A5 | FSI Executive Summary: Principles for operational resilience *(length outlier, 3 pp)* | https://www.bis.org/fsi/fsisummaries/op_resilience.pdf | 2026-07-29 | © BIS; reproduction permitted with attribution | A |

## Cluster B — RAG architecture and retrieval (arXiv)

| Id | Title | URL | Retrieved | Licence | Cluster |
|----|-------|-----|-----------|---------|---------|
| B1 | Lewis et al., *Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks* | https://arxiv.org/pdf/2005.11401 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | B |
| B2 | Karpukhin et al., *Dense Passage Retrieval for Open-Domain Question Answering* | https://arxiv.org/pdf/2004.04906 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | B |
| B3 | Gao et al., *Retrieval-Augmented Generation for Large Language Models: A Survey* *(length outlier, 30+ pp)* | https://arxiv.org/pdf/2312.10997 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | B |
| B4 | Sarthi et al., *RAPTOR: Recursive Abstractive Processing for Tree-Organized Retrieval* | https://arxiv.org/pdf/2401.18059 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | B |
| B5 | Edge et al., *From Local to Global: A Graph RAG Approach to Query-Focused Summarization* | https://arxiv.org/pdf/2404.16130 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | B |

## Cluster C — Graph RAG and evaluation (arXiv; deliberately adjacent to B)

| Id | Title | URL | Retrieved | Licence | Cluster |
|----|-------|-----|-----------|---------|---------|
| C1 | Peng et al., *Graph Retrieval-Augmented Generation: A Survey* | https://arxiv.org/pdf/2408.08921 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | C |
| C2 | Guo et al., *LightRAG: Simple and Fast Retrieval-Augmented Generation* | https://arxiv.org/pdf/2410.05779 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | C |
| C3 | Han et al., *Retrieval-Augmented Generation with Graphs (GraphRAG)* | https://arxiv.org/pdf/2501.00309 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | C |
| C4 | Es et al., *RAGAS: Automated Evaluation of Retrieval Augmented Generation* — v1 *(pairs with C5)* | https://arxiv.org/pdf/2309.15217v1 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | C |
| C5 | Es et al., *RAGAS: Automated Evaluation of Retrieval Augmented Generation* — v2 *(pairs with C4)* | https://arxiv.org/pdf/2309.15217v2 | 2026-07-29 | arXiv open access (per-paper licence on abstract page) | C |

## Cluster D — Zero-trust & cybersecurity guidance (NIST / CISA) — our choice per brief §4

One coherent topic (US federal cybersecurity guidance) chosen to sit far from clusters A–C in
embedding space, so the recommender is graded on a topic the instructor has not tuned against.
Identifier-dense titles ("SP 800-207", "CSF 2.0") also feed the gold set's lexical-query quota.

| Id | Title | URL | Retrieved | Licence | Cluster |
|----|-------|-----|-----------|---------|---------|
| D1 | NIST Cybersecurity Framework (CSF) 2.0 — NIST CSWP 29 | https://nvlpubs.nist.gov/nistpubs/CSWP/NIST.CSWP.29.pdf | 2026-07-31 | US Government work, public domain | D |
| D2 | Zero Trust Architecture — NIST SP 800-207 | https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-207.pdf | 2026-07-31 | US Government work, public domain | D |
| D3 | Computer Security Incident Handling Guide — NIST SP 800-61r2 | https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-61r2.pdf | 2026-07-31 | US Government work, public domain | D |
| D4 | Digital Identity Guidelines: Authentication and Lifecycle Management — NIST SP 800-63B | https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-63b.pdf | 2026-07-31 | US Government work, public domain | D |
| D5 | Zero Trust Maturity Model v2.0 — CISA | https://www.cisa.gov/sites/default/files/2023-04/zero_trust_maturity_model_v2_508.pdf | 2026-07-31 | US Government work, public domain | D |

> Note (2026-07-31): SP 800-61 rev 3 exists but nvlpubs serves no stable r3 PDF URL at retrieval
> time; r2 (2012) is used — its age is a feature, stretching the recency signal's date range.

Permitted-source check (brief §4): all five are US federal government publications (NIST is a
Department of Commerce agency; CISA is part of DHS), i.e. regulator/standards-body material and
public domain. Nothing paywalled, nothing confidential.

## Manufactured artefacts (not fetched, not committed)

| Id | Description | Provenance |
|----|-------------|------------|
| A1-SCAN | `corpus/A1-scanned.pdf` — pages 1–12 of A1 rasterised at 200 DPI with Docnet.Core and reassembled image-only (brief §4.1) | Generated by `dotnet run --project src/Athena.Ingestion -- rasterise`; ingested under DocId `A1-SCAN` to measure the OCR Delta in Part F |
