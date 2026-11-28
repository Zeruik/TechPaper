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
HC3_DIGESTS = {
    "en/config.json": "d5622cc0da0488f40673b4524a7ad128e6124da5c14cacec39ead4ad42401ddb",
    "en/model_quantized.onnx": "219d37a3d815a969e92664a9f2b2101cb754a1e1e0ce2ace81a0c3cdb149ff7c",
    "en/tokenizer.json": "fac96121c0f9c36533e15c1ed9ebd3fe6e37a869247617866ca6a8df40e48e43",
    "zh/config.json": "e9dc94d21fe2dff03e7fd06d3826e4607dce8c164988349bdac329f3af2f27a2",
    "zh/model.onnx": "f2f3c2034159a893ab51efc03a54fbf0c7c353b750c2938f730c5844ef6558bd",
    "zh/tokenizer.json": "93295ddede9ff05639600ddbb1d87a542bf0e72c9e1c8f0bf639e8142eaa0d50",
}


def controls(abstracts, engineering, translations, translation_start=0):
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
            lines = archive.read(name).decode("utf-8-sig").splitlines()
            if translation_start < 0 or translation_start + 100 > len(lines):
                raise ValueError("Expected 100 aligned translation rows")
            for text in lines[translation_start:translation_start + 100]:
                yield "en", suffix, text


def verify_assets(folder, digests):
    for name, expected in digests.items():
        with (folder / name).open("rb") as stream:
            actual = hashlib.file_digest(stream, "sha256").hexdigest()
        if actual != expected:
            raise ValueError("Unexpected candidate asset: " + name)


def decision(low, high):
    return "AI" if low >= .95 else "HUMAN" if high <= .05 else "abstain"


def run(args):
    verify_assets(args.model_dir, DIGESTS)
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
    corroborators = {}
    if args.hc3_dir:
        verify_assets(args.hc3_dir, HC3_DIGESTS)
        for lang, filename in (("en", "model_quantized.onnx"), ("zh", "model.onnx")):
            folder = args.hc3_dir / lang
            labels = json.loads((folder / "config.json").read_bytes())["id2label"]
            if set(labels.values()) != {"ChatGPT", "Human"}:
                raise ValueError("Unexpected HC3 label mapping")
            ai_index = next(int(k) for k, v in labels.items() if v == "ChatGPT")
            tokenizer = Tokenizer.from_file(str(folder / "tokenizer.json"))
            tokenizer.no_padding()
            tokenizer.no_truncation()
            session = ort.InferenceSession(str(folder / filename), opts, providers=["CPUExecutionProvider"])
            corroborators[lang] = session, tokenizer, ai_index
    counts = defaultdict(Counter)
    for lang, role, text in controls(args.abstracts, args.engineering, args.translations, args.translation_start):
        label = decision(*document_scores(model, tok, text, 1, max_length=256))
        if corroborators:
            session, tokenizer, ai_index = corroborators[lang]
            other = decision(*document_scores(session, tokenizer, text, ai_index))
            if other != label:
                label = "abstain"
        counts[lang, role][label] += 1
    print("language cohort total AI HUMAN abstain")
    for group, row in sorted(counts.items()):
        print(*group, sum(row.values()), *(row[k] for k in ("AI", "HUMAN", "abstain")))


if __name__ == "__main__":
    parser = ArgumentParser(description=__doc__)
    for name in ("model_dir", "abstracts", "engineering", "translations"):
        parser.add_argument(name, type=Path)
    parser.add_argument("--hc3-dir", type=Path, help="Require agreement with the corresponding HC3 checkpoint")
    parser.add_argument("--translation-start", type=int, default=0, help="Zero-based first of 100 aligned rows")
    run(parser.parse_args())
