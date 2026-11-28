"""Assess English translation-calibrated alerts on aligned rows and engineering prose."""
import argparse
from collections import Counter, defaultdict
import hashlib
from pathlib import Path
from zipfile import ZipFile

import onnxruntime as ort
from tokenizers import Tokenizer

from calibration_benchmark import document_scores, upper_control
from multilingual_transfer import controls, verify_assets, DIGESTS


def key(text):
    return hashlib.sha256(" ".join(text.casefold().split()).encode()).digest()


def run(model, abstracts, engineering, translations):
    verify_assets(model, DIGESTS)
    ort.disable_telemetry_events()
    options = ort.SessionOptions()
    options.intra_op_num_threads = 3
    session = ort.InferenceSession(str(model / "model_q4.onnx"), options, providers=["CPUExecutionProvider"])
    tokenizer = Tokenizer.from_file(str(model / "tokenizer.json"))
    tokenizer.no_padding()
    tokenizer.no_truncation()

    def score(text):
        return document_scores(session, tokenizer, text, 1, max_length=256)

    with ZipFile(translations) as archive:
        versions = {}
        for suffix in ("Reference-HT.txt", "Reference-PE.txt", "Combo-4.txt"):
            name, = [n for n in archive.namelist() if n.endswith(suffix)]
            versions[suffix] = archive.read(name).decode("utf-8-sig").splitlines()
    if len({len(rows) for rows in versions.values()}) != 1 or len(versions["Reference-HT.txt"]) < 300:
        raise ValueError("Missing aligned translation rows")
    calibration = [score(t) for t in versions["Reference-HT.txt"][:100]]
    threshold = max(.95, upper_control(calibration))
    print("Calibration HT N=100; thresholds", .95, repr(threshold), "flags",
          sum(lo > .95 for lo, _ in calibration), sum(lo > threshold for lo, _ in calibration), flush=True)

    seen = {key(t) for rows in versions.values() for t in rows[:100]}
    blocked = {i for i in range(200,300) if any(key(rows[i]) in seen for rows in versions.values())}
    groups = defaultdict(Counter)
    positions = Counter()
    for lang, role, text in controls(abstracts, engineering, translations, translation_start=200):
        if lang != "en":
            continue
        if role in versions:
            i = 200 + positions[role]
            positions[role] += 1
            if i in blocked:
                continue
        if key(text) in seen:
            raise ValueError("Evaluation overlap with calibration")
        low, high = score(text)
        for variant, limit in (("A", .95), ("B", threshold)):
            label = "AI" if low > limit else "HUMAN" if high <= .05 else "abstain"
            groups[role,variant][label] += 1
    print("Excluded aligned rows:", len(blocked))
    print("cohort variant N AI HUMAN abstain")
    for (role, variant), counts in sorted(groups.items()):
        print(role, variant, sum(counts.values()), *(counts[k] for k in ("AI","HUMAN","abstain")))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("model", "abstracts", "engineering", "translations"):
        parser.add_argument(name,type=Path)
    run(**vars(parser.parse_args()))
