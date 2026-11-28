"""Evaluate a local two-label HC3 ONNX conversion; no remote inference or downloads."""
import argparse
from collections import Counter
import json
from pathlib import Path
from zipfile import ZipFile

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer
from corpus_benchmark import documents


def balanced_windows(encoded, max_length=512):
    fields = {"input_ids": encoded.ids, "attention_mask": encoded.attention_mask,
              "token_type_ids": encoded.type_ids}
    if len(encoded.ids) <= max_length:
        yield fields
        return
    # Both pinned tokenizers wrap a single sequence with one prefix and one suffix.
    mask = encoded.special_tokens_mask
    if mask != [1] + [0] * (len(mask) - 2) + [1]:
        raise ValueError("Unexpected single-sequence special token layout")
    tokens = len(mask) - 2
    capacity = max_length - 2
    num_windows = (tokens + capacity - 1) // capacity
    for i in range(num_windows):
        start = 1 + i * tokens // num_windows
        end = 1 + (i + 1) * tokens // num_windows
        yield {name: [values[0], *values[start:end], values[-1]] for name, values in fields.items()}


def window_label(session, fields, labels):
    feed = {item.name: np.array([fields[item.name]], dtype=np.int64)
            for item in session.get_inputs()}
    logits = session.run(None, feed)[0][0]
    weights = np.exp(logits - logits.max())
    confidence = weights.max() / weights.sum()
    return labels[str(logits.argmax())] if confidence >= .95 else "abstain"


def evaluate(folder, model, groups, windowed=False):
    config = json.loads((folder / "config.json").read_bytes())
    labels = config["id2label"]
    if set(labels.values()) != {"Human", "ChatGPT"}:
        raise ValueError("Unexpected label mapping")
    options = ort.SessionOptions()
    options.intra_op_num_threads = 3
    session = ort.InferenceSession(str(model), options, providers=["CPUExecutionProvider"])
    tokenizer = Tokenizer.from_file(str(folder / "tokenizer.json"))
    tokenizer.no_truncation()
    tokenizer.no_padding()
    for name, texts in groups.items():
        counts = Counter()
        for text in texts:
            encoded = tokenizer.encode(text)
            if len(encoded.ids) > 512 and not windowed:
                counts["overlength"] += 1
                continue
            votes = {window_label(session, fields, labels) for fields in balanced_windows(encoded)}
            decision = votes.pop() if len(votes) == 1 else "abstain"
            counts[decision] += 1
        print(name, len(texts), *(counts[k] for k in ("ChatGPT", "Human", "abstain", "overlength")), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Columns: cohort N AI Human abstain overlength")
    parser.add_argument("model", type=Path, help="Local ONNX with tokenizer.json and config.json beside it")
    parser.add_argument("language", choices=("en","zh"))
    parser.add_argument("groups", type=Path)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--windowed", action="store_true", help="Score every token in balanced windows and require unanimous decisions")
    args = parser.parse_args()
    groups = {k:v for k,v in json.loads(args.groups.read_bytes()).items() if k.startswith(args.language+" ")}
    if args.archive and args.language == "en":
        with ZipFile(args.archive) as data:
            for name in ("CS224N_real_145","CS224N_gpt3_145","TOEFL_real_91","TOEFL_gpt4polished_91"):
                groups[name] = documents(data, name)[:32]
    evaluate(args.model.parent, args.model, groups, args.windowed)
