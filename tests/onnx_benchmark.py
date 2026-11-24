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


def evaluate(folder, model, groups):
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
            if len(encoded.ids) > 512:
                counts["overlength"] += 1
                continue
            fields = {"input_ids": encoded.ids, "attention_mask": encoded.attention_mask,
                      "token_type_ids": encoded.type_ids}
            feed = {item.name: np.array([fields[item.name]], dtype=np.int64)
                    for item in session.get_inputs()}
            logits = session.run(None, feed)[0][0]
            weights = np.exp(logits - logits.max())
            confidence = weights.max() / weights.sum()
            counts[labels[str(logits.argmax())] if confidence >= .95 else "abstain"] += 1
        print(name, len(texts), *(counts[k] for k in ("ChatGPT", "Human", "abstain", "overlength")), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Columns: cohort N AI Human abstain overlength")
    parser.add_argument("model", type=Path, help="Local ONNX with tokenizer.json and config.json beside it")
    parser.add_argument("language", choices=("en","zh"))
    parser.add_argument("groups", type=Path)
    parser.add_argument("--archive", type=Path)
    args = parser.parse_args()
    groups = {k:v for k,v in json.loads(args.groups.read_bytes()).items() if k.startswith(args.language+" ")}
    if args.archive and args.language == "en":
        with ZipFile(args.archive) as data:
            for name in ("CS224N_real_145","CS224N_gpt3_145","TOEFL_real_91","TOEFL_gpt4polished_91"):
                groups[name] = documents(data, name)[:32]
    evaluate(args.model.parent, args.model, groups)
