"""Compare fixed neural alerts with an independent paired-style corroboration rule."""
import argparse
from collections import Counter
import json
from pathlib import Path
from statistics import median
from zipfile import ZipFile

import onnxruntime as ort
from tokenizers import Tokenizer

from bilingual_holdout import collect
from calibration_benchmark import document_scores
from corpus_benchmark import documents
from style_benchmark import fingerprint, fit, training_split


def evaluate(models, hc3, papers, abstracts, archive):
    groups = collect(abstracts)
    for lang, model in models.items():
        pairs, held, seen = training_split(hc3, lang)
        style = fit(pairs, lang)
        controls = [[h for h, _ in held], [p["h"][lang] for p in papers]]
        if lang == "en":
            controls.extend(documents(archive, k) for k in ("HewlettStudentEssay_real_88", "HewlettStudentEssay_GPTsimplify_88"))
        floor = max(median(style(t) for t in group) for group in controls)
        excluded = seen | {fingerprint(t) for group in controls for t in group}
        labels = json.loads((model.parent / "config.json").read_bytes())["id2label"]
        if set(labels.values()) != {"ChatGPT", "Human"}:
            raise ValueError("Unexpected label mapping")
        ai = next(int(k) for k, v in labels.items() if v == "ChatGPT")
        tokenizer = Tokenizer.from_file(str(model.parent / "tokenizer.json"))
        tokenizer.no_padding()
        tokenizer.no_truncation()
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = 3
        session = ort.InferenceSession(str(model), opts, providers=["CPUExecutionProvider"])
        cohorts = {k:v for k,v in groups.items() if k.startswith(lang+" ")}
        if lang == "en":
            for k in ("TOEFL_real_91", "TOEFL_gpt4polished_91", "CS224N_real_145", "CS224N_gpt3_145",
                      "CollegeEssay_real_70", "CollegeEssay_gpt3_31",
                      "CS224N_gpt3PromptEng_145", "CollegeEssay_gpt3PromptEng_31"):
                cohorts[k] = documents(archive, k)
        for name, texts in cohorts.items():
            counts = Counter()
            for t in texts:
                assert fingerprint(t) not in excluded
                low, _ = document_scores(session, tokenizer, t, ai)
                if low >= .95:
                    counts["neural"] += 1
                    if style(t) > floor:
                        counts["both"] += 1
            print(name, "N", len(texts), "neural", counts["neural"], "both", counts["both"],
                  "combined abstain", len(texts)-counts["both"], flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("english_model", "chinese_model", "hc3", "papers", "abstracts", "archive"):
        parser.add_argument(key, type=Path)
    args = parser.parse_args()
    with ZipFile(args.archive) as archive:
        evaluate({"en":args.english_model, "zh":args.chinese_model}, args.hc3,
                 json.loads(args.papers.read_bytes()), args.abstracts, archive)
