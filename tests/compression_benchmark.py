"""Evaluate a gzip nearest-neighbor candidate offline. Output contains counts only."""
import argparse
from collections import Counter
import gzip
import hashlib
import heapq
import json
from pathlib import Path
from zipfile import ZipFile

from corpus_benchmark import documents, training_rows


def encode(text):
    return " ".join(text.split()).encode("utf-8")


def compressed_size(data):
    return len(gzip.compress(data, compresslevel=6, mtime=0))


def references(folder, language):
    rows = sorted(training_rows(folder, language),
                  key=lambda pair: hashlib.sha256(encode(pair[0])).digest())
    result = []
    for pair in rows[:96]:
        for label, text in zip(("HUMAN", "AI"), pair):
            data = encode(text)
            result.append((data, compressed_size(data), label))
    return result


def classify(text, refs):
    sample = encode(text)
    length = compressed_size(sample)
    distances = []
    for index, (reference, size, label) in enumerate(refs):
        joint = compressed_size(sample + b" " + reference)
        distance = (joint - min(size, length)) / max(size, length)
        distances.append((distance, index, label))
    votes = {label for _, _, label in heapq.nsmallest(5, distances)}
    return votes.pop() if len(votes) == 1 else "abstain"


def evaluate(folder, source_groups, archive):
    groups = json.loads(source_groups.read_text(encoding="utf-8-sig"))
    with ZipFile(archive) as data:
        for cohort in ("CS224N_real_145", "CS224N_gpt3_145", "TOEFL_real_91",
                       "TOEFL_gpt4polished_91", "CollegeEssay_real_70", "CollegeEssay_gpt3_31"):
            groups["en "+cohort] = documents(data, cohort)
    for lang in ("zh", "en"):
        refs = references(folder, lang)
        for name, texts in groups.items():
            if name.startswith(lang+" "):
                counts = Counter(classify(text, refs) for text in texts)
                print(name, len(texts), *(counts[k] for k in ("AI","HUMAN","abstain")), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Columns: cohort N AI HUMAN abstain")
    parser.add_argument("hc3", type=Path)
    parser.add_argument("groups", type=Path, help="JSON mapping 'zh/en cohort' to a list of texts")
    parser.add_argument("archive", type=Path)
    args = parser.parse_args()
    evaluate(args.hc3, args.groups, args.archive)
