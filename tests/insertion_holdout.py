"""Construct an English source-transfer stress test from a local Liang research archive."""
from argparse import ArgumentParser
from collections import defaultdict
import json
from pathlib import Path
import sys
from zipfile import ZipFile

from corpus_benchmark import documents
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
from review import SENT


def sentences(text):
    return [m.group().strip() for m in SENT.finditer(text) if m.group().strip()]


def build(archive, output):
    isolated, contexts = defaultdict(list), defaultdict(list)
    with ZipFile(archive) as z:
        human = documents(z, "CollegeEssay_real_70")
        generated = documents(z, "CollegeEssay_gpt3_31")
    used = 0
    for i, (h, g) in enumerate(zip(human, generated)):
        original, synthetic = sentences(h), sentences(g)
        size = 1 if i % 2 == 0 else 3
        if len(original) < 6 or len(synthetic) < size:
            continue
        pos = len(original) // 2
        start = (len(synthetic) - size) // 2
        rows = original[:pos] + synthetic[start:start+size] + original[pos:]
        for j in range(pos-2, pos+size+2):
            if pos <= j < pos+size:
                role = "generated"
            elif j in (pos-1, pos+size):
                role = "adjacent human"
            else:
                role = "second human"
            name = f"en {size}-insert {role}"
            isolated[name].append(rows[j])
            contexts[name].append(" ".join(rows[j-1:j+2]))
        used += 1
    output.mkdir(parents=True, exist_ok=True)
    for name, values in (("isolated", isolated), ("context", contexts)):
        (output / (name + ".json")).write_text(json.dumps(values), encoding="utf8")
    print("eligible pairs", used, "of", len(generated))


if __name__ == "__main__":
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("output", type=Path, help="Local-only working directory; outputs contain excerpts")
    args = parser.parse_args()
    build(args.archive, args.output)
