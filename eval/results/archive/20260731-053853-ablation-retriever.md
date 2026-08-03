# Ablation: ablation-retriever (2026-07-31 05:38:53Z)

Held constant: chunker=fixed; docvec=centroid.

| Metric | dense | bm25 | hybrid | hybrid-rerank |
|---|---|---|---|---|
| AbstentionAccuracy | 1.000 | 1.000 | 1.000 | 1.000 |
| AnswerCorrectness | 0.929 | 0.964 | 0.929 | 1.000 |
| CitationViolationRate | 0.429 | 0.357 | 0.357 | 0.429 |
| ContextPrecision@6 | 0.155 | 0.190 | 0.179 | 0.226 |
| ContextRecall@6 | 0.643 | 0.786 | 0.714 | 0.786 |
| Faithfulness | 0.964 | 1.000 | 0.881 | 0.964 |
| OcrDelta | 0.000 | 0.000 | 0.333 | 0.000 |
