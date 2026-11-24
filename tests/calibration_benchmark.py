"""Test two source-level calibration rules on local, provenance-labelled English cohorts."""
import argparse
from collections import Counter
import json
from math import ceil
from pathlib import Path
from zipfile import ZipFile

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

from corpus_benchmark import documents
from onnx_benchmark import balanced_windows


def document_scores(session, tokenizer, text, ai_index):
    scores = []
    for fields in balanced_windows(tokenizer.encode(text)):
        feed = {item.name: np.asarray([fields[item.name]], dtype=np.int64)
                for item in session.get_inputs()}
        logits = session.run(None, feed)[0][0].astype(np.float64)
        weights = np.exp(logits - logits.max())
        scores.append(float(weights[ai_index] / weights.sum()))
    return min(scores), max(scores)


def upper_control(scores):
    # Strictly exceed the order statistic to flag a document (ties abstain).
    rank = ceil(.95 * len(scores))
    return sorted(low for low, _ in scores)[rank - 1]


def benchmark(model, archive):
    labels = json.loads((model.parent / "config.json").read_bytes())["id2label"]
    if set(labels.values()) != {"ChatGPT", "Human"}:
        raise ValueError("Unexpected label mapping")
    ai_index = next(int(i) for i, name in labels.items() if name == "ChatGPT")
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = 3
    session = ort.InferenceSession(str(model), opts, providers=["CPUExecutionProvider"])
    tokenizer = Tokenizer.from_file(str(model.parent / "tokenizer.json"))
    tokenizer.no_padding()
    tokenizer.no_truncation()

    def cohort(name):
        return [document_scores(session, tokenizer, text, ai_index) for text in documents(archive, name)]

    originals = cohort("HewlettStudentEssay_real_88")
    assisted = cohort("HewlettStudentEssay_GPTsimplify_88")
    a = max(.95, upper_control(originals))
    b = max(a, upper_control(assisted))
    print("A threshold", a, "B threshold", b, flush=True)
    tests = ("TOEFL_real_91", "TOEFL_gpt4polished_91", "CS224N_real_145", "CS224N_gpt3_145",
             "CollegeEssay_real_70", "CollegeEssay_gpt3_31")
    for name in tests:
        scores = cohort(name)
        for variant, threshold in (("A", a), ("B", b)):
            counts = Counter("AI" if low > threshold else "HUMAN" if high <= .05 else "abstain"
                             for low, high in scores)
            print(variant, name, len(scores), *(counts[k] for k in ("AI", "HUMAN", "abstain")), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path)
    parser.add_argument("archive", type=Path)
    args = parser.parse_args()
    with ZipFile(args.archive) as source:
        benchmark(args.model, source)
