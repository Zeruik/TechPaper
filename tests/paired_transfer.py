"""Test a frozen multilingual detector with author/journal-disjoint calibration folds."""
import argparse
from collections import Counter, defaultdict
from pathlib import Path
from xml.etree import ElementTree

import onnxruntime as ort
from tokenizers import Tokenizer

from author_holdout import authors, components, normalized
from bilingual_holdout import ARTICLES, source_texts
from calibration_benchmark import document_scores
from multilingual_transfer import DIGESTS, verify_assets


def independent_folds(folder):
    sources = {}
    for paper in ARTICLES:
        path = folder / (paper + ".xml")
        journal = ElementTree.parse(path).findtext("./front/journal-meta/journal-title-group/journal-title")
        if not journal or not normalized(journal):
            raise ValueError("Missing journal")
        sources[paper] = authors(path) | {("journal", normalized(journal))}
    folds = components(sources)
    if len(folds) < 2:
        raise ValueError("No independent calibration fold")
    return folds


def evaluate(folder, model):
    folds = independent_folds(folder)
    print("Held-out fold sizes:", [len(fold) for fold in folds])
    verify_assets(model, DIGESTS)
    ort.disable_telemetry_events()
    options = ort.SessionOptions()
    options.intra_op_num_threads = 3
    session = ort.InferenceSession(str(model / "model_q4.onnx"), options, providers=["CPUExecutionProvider"])
    tokenizer = Tokenizer.from_file(str(model / "tokenizer.json"))
    tokenizer.no_padding()
    tokenizer.no_truncation()
    scores = {(paper, lang, role): document_scores(session, tokenizer, text, 1, max_length=256)
              for paper, lang, role, text in source_texts(folder)}
    for lang in ("en", "zh"):
        counts = defaultdict(Counter)
        ranks = Counter()
        for test in folds:
            limits = {}
            for variant, roles in (("primary-only", {"primary"}),
                                   ("with-translations", {"primary", "translation"})):
                calibration = [low for (paper, language, role), (low, _) in scores.items()
                               if language == lang and paper not in test and role in roles]
                limits[variant] = max([.95, *calibration])
                print(lang, variant, "held-out", len(test), "calibration N", len(calibration),
                      "threshold", limits[variant])
            for paper in test:
                human_role, = [role for p, language, role in scores if p == paper and language == lang and role != "generated"]
                human = scores[paper, lang, human_role][0]
                generated = scores[paper, lang, "generated"][0]
                ranks[human_role, "higher" if generated > human else "lower" if generated < human else "tie"] += 1
                for role in (human_role, "generated"):
                    low, high = scores[paper, lang, role]
                    for variant, threshold in limits.items():
                        label = "AI" if low > threshold else "HUMAN" if high <= .05 else "abstain"
                        counts[variant, role][label] += 1
        for (variant, role), group in sorted(counts.items()):
            print(lang, variant, role, "AI/HUMAN/abstain", *(group[k] for k in ("AI", "HUMAN", "abstain")))
        for role in ("primary", "translation"):
            print(lang, role, "paired higher/tie/lower", *(ranks[role, k] for k in ("higher", "tie", "lower")))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("model", type=Path)
    args = parser.parse_args()
    evaluate(args.folder, args.model)
