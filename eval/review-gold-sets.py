"""Gold-set review aid (brief §11.1 / §11.2).

The gold sets are HAND-authored judgement and the marking rubric treats them as the author's
own work, so they must be reviewed rather than trusted. This script does not grade anything —
it prints each case beside the actual source text of its gold page(s) so a human can confirm,
in one pass, that the labelled page really does support the labelled answer.

    python eval/review-gold-sets.py            # QA cases, with page text
    python eval/review-gold-sets.py --rec      # recommender seeds, with titles + topics
    python eval/review-gold-sets.py --qa 14    # one QA case in full

Requires pypdf (pip install pypdf) and the corpus PDFs present in corpus/.
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CORPUS = ROOT / "corpus"

try:
    import pypdf
except ImportError:
    sys.exit("pypdf not installed. Run: pip install pypdf")

sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def page_text(doc_id: str, page: int) -> str:
    pdf = CORPUS / f"{doc_id}.pdf"
    if not pdf.exists():
        return f"[{pdf.name} not present — run: dotnet run --project src/Athena.Ingestion -- fetch]"
    reader = pypdf.PdfReader(str(pdf))
    if page < 1 or page > len(reader.pages):
        return f"[page {page} out of range: {pdf.name} has {len(reader.pages)} pages]"
    return re.sub(r"\s+", " ", reader.pages[page - 1].extract_text() or "").strip()


def titles() -> dict[str, str]:
    manifest = json.loads((CORPUS / "manifest.json").read_text(encoding="utf-8-sig"))
    return {d["id"]: d["title"] for d in manifest["documents"]}


def review_qa(only: int | None) -> None:
    cases = json.loads((ROOT / "eval" / "qa-cases.json").read_text(encoding="utf-8"))["cases"]
    answerable = sum(1 for c in cases if c["isAnswerable"])
    cluster_d = sum(1 for c in cases if c["goldDocId"].startswith("D"))
    print(f"QA GOLD SET — {len(cases)} cases: {answerable} answerable, "
          f"{len(cases) - answerable} unanswerable; {cluster_d} from cluster D")
    print("Brief §11.1 requires: 25 cases (20 answerable / 5 unanswerable), >=4 from cluster D,")
    print(">=2 questions whose answer differs between a draft and its final.\n")

    for i, c in enumerate(cases, 1):
        if only and i != only:
            continue
        print("=" * 100)
        print(f"qa{i:02d}  [{'ANSWERABLE' if c['isAnswerable'] else 'UNANSWERABLE'}]"
              f"  gold={c['goldDocId'] or '(none)'} pages={c['goldPages']}")
        print(f"  Q: {c['question']}")
        print(f"  A: {c['expectedAnswer']}")
        if not c["isAnswerable"]:
            print("  CHECK: is this genuinely absent from ALL FOUR clusters "
                  "(A regulation, B/C RAG research, D NIST/CISA security)?")
            continue
        for p in c["goldPages"]:
            text = page_text(c["goldDocId"], p)
            print(f"\n  --- {c['goldDocId']} p{p} " + "-" * 60)
            print("  " + (text[:900] if only else text[:420]) + ("..." if len(text) > 420 else ""))
        print("\n  CHECK: does the page text above actually state the expected answer?")


def review_rec() -> None:
    data = json.loads((ROOT / "eval" / "rec-cases.json").read_text(encoding="utf-8"))
    cases, t = data["cases"], titles()
    straddling = [c for c in cases
                  if c["seedDocId"][0] in "BC"
                  and {d[0] for d in c["relevantDocIds"]} & {"B", "C"} == {"B", "C"}]
    print(f"RECOMMENDER GOLD SET — {len(cases)} seeds; "
          f"{len(straddling)} straddle the B/C boundary ({[c['seedDocId'] for c in straddling]})")
    print("Brief §11.2 requires: 8 seeds, >=2 straddling clusters B and C.\n")
    print("LABELLING POLICY IN THIS FILE: a seed's own superseded lineage sibling is NOT")
    print("labelled relevant. This makes Duplicate Leakage's target 0 and means ablation 4")
    print("cannot penalise dedup. If you disagree, change the labels and re-run:")
    print("  dotnet run --project src/Athena.Eval -- rec\n")

    for c in cases:
        print("=" * 100)
        print(f"SEED {c['seedDocId']}: {t.get(c['seedDocId'], '?')}")
        if "_why" in c:
            print(f"  rationale: {c['_why']}")
        print("  labelled relevant:")
        for d in c["relevantDocIds"]:
            print(f"    {d:4} {t.get(d, '?')}")
        excluded = [d for d in t
                    if d != c["seedDocId"] and d not in c["relevantDocIds"]
                    and d[0] == c["seedDocId"][0]]
        print(f"  same-cluster documents NOT labelled: {excluded}")
        print("  CHECK: would you actually hand these to an analyst who just read the seed?")


if __name__ == "__main__":
    args = sys.argv[1:]
    if "--rec" in args:
        review_rec()
    else:
        idx = args.index("--qa") + 1 if "--qa" in args else 0
        review_qa(int(args[idx]) if idx else None)
