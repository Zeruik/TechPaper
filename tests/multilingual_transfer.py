"""Evaluate the pinned multilingual Q4 candidate locally, without tuning on the controls."""
from argparse import ArgumentParser
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile

import onnxruntime as ort
from tokenizers import Tokenizer

from bilingual_holdout import source_texts
from calibration_benchmark import document_scores
from generator_holdout import LANGUAGES, MODELS, TOPICS


DIGESTS = {
    "model_q4.onnx": "aafd575dbc7e7cc1fe87c043c8cfb7d907adf92dce0a36d40bcc340637edefea",
    "tokenizer.json": "1d237c4974cfea16dffbc5035028fea5b47ad8b653af90c908b7a35bdc8b0d90",
    "config.json": "2b1c25ed502921c2e92c2cf1dfa4702d55ef9f9cf40461dac0386a7dc2092d3b",
}


def controls(abstracts, engineering, translations):
    for _, language, role, text in source_texts(abstracts):
        yield language, "abstract-" + role, text
    for generator, digest in MODELS.items():
        for i in range(len(TOPICS)):
            for language in LANGUAGES:
                row = json.loads((engineering / f"{digest[:12]}-{i}-{language}.json").read_bytes())
                if ((row["model"], row["digest"], row["topic"], row["language"], row["finished"])
                        != (generator, digest, i, language, "stop") or not row["text"].strip()):
                    raise ValueError("Incomplete engineering control")
                yield language, generator, row["text"]
    with ZipFile(translations) as archive:
        for suffix in ("Reference-HT.txt", "Reference-PE.txt", "Combo-4.txt"):
            name, = [n for n in archive.namelist() if n.endswith(suffix)]
            for text in archive.read(name).decode("utf-8-sig").splitlines()[:100]:
                yield "en", suffix, text


def run(args):
    for name, expected in DIGESTS.items():
        if hashlib.sha256((args.model_dir / name).read_bytes()).hexdigest() != expected:
            raise ValueError("Unexpected candidate asset: " + name)
    config = json.loads((args.model_dir / "config.json").read_bytes())
    if config["id2label"] != {"0": "human", "1": "ai"}:
        raise ValueError("Unexpected class labels")
    tok = Tokenizer.from_file(str(args.model_dir / "tokenizer.json"))
    tok.no_padding()
    tok.no_truncation()
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = 3
    ort.disable_telemetry_events()
    model = ort.InferenceSession(str(args.model_dir / "model_q4.onnx"), opts, providers=["CPUExecutionProvider"])
    counts = defaultdict(Counter)
    for lang, role, text in controls(args.abstracts, args.engineering, args.translations):
        low, high = document_scores(model, tok, text, 1, max_length=256)
        label = "AI" if low >= .95 else "HUMAN" if high <= .05 else "abstain"
        counts[lang, role][label] += 1
    print("language cohort total AI HUMAN abstain")
    for group, row in sorted(counts.items()):
        print(*group, sum(row.values()), *(row[k] for k in ("AI", "HUMAN", "abstain")))


if __name__ == "__main__":
    parser = ArgumentParser(description=__doc__)
    for name in ("model_dir", "abstracts", "engineering", "translations"):
        parser.add_argument(name, type=Path)
    run(parser.parse_args())
