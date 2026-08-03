# Ablation: ablation-retriever (2026-08-01 06:21:38Z)

Held constant: chunker=fixed; docvec=centroid.

| Metric | dense | bm25 | hybrid | hybrid-rerank |
|---|---|---|---|---|
| AbstentionAccuracy | 1.000 | 1.000 | 1.000 | 1.000 |
| AnswerCorrectness | 0.950 | 0.900 | 0.875 | 0.975 |
| CitationViolationRate | 0.000 | 0.000 | 0.000 | 0.000 |
| ContextPrecision@6 | 0.150 | 0.183 | 0.167 | 0.192 |
| ContextRecall@6 | 0.650 | 0.650 | 0.700 | 0.750 |
| Faithfulness | 0.950 | 0.975 | 0.942 | 0.975 |
| OcrDelta | 0.000 | 0.000 | 0.333 | 0.000 |
