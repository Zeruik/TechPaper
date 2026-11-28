"""Offline paired style-feature candidate. Emits aggregate counts only."""
import argparse
from collections import Counter
import hashlib
import json
from math import ceil
from pathlib import Path
import re
from zipfile import ZipFile

import numpy as np

from bilingual_holdout import source_texts
from corpus_benchmark import documents, training_rows


WORDS = {
    "en": "the a an of to for from by with and or but as if that which this these is are was were have has it they we".split(),
    "zh": "的 了 地 得 在 与 和 或 而 但 将 被 为 对 于 以 由 其 该 这 这些 我们 可以 可能".split(),
}


def features(text, lang):
    units = re.findall(r"[A-Za-z]+|[\u3400-\u9fff]|\d+", text.casefold(), re.I)
    counts = Counter(units)
    n = max(len(units), 1)
    freq = [counts[w] if lang == "en" else text.count(w) for w in WORDS[lang]]
    for marks in (",，", ";；", ":：", "()（）", "!?！？", ".。"):
        freq.append(sum(text.count(c) for c in marks))
    return np.array(freq, dtype=float) / n


def fingerprint(text):
    return hashlib.sha256(" ".join(text.casefold().split()).encode()).digest()


def fit(pairs, lang):
    # Pair differencing removes the intercept and compares answers to the same question.
    delta = np.asarray([features(g, lang)-features(h, lang) for h, g in pairs])
    scale = np.maximum(delta.std(axis=0), .001)
    x = delta / scale
    weights = np.zeros(x.shape[1])
    for _ in range(400):
        weights += .1 * (x.T @ (1/(1+np.exp(np.clip(x@weights, -50, 50)))) / len(x) - .1*weights)
    return lambda text: float((features(text, lang)/scale) @ weights)


def cutoff(scores):
    return sorted(scores)[ceil(.95*len(scores))-1]


def training_split(hc3, lang):
    pairs, held, seen = [], [], set()
    for human, generated in training_rows(hc3, lang):
        keys = {fingerprint(human), fingerprint(generated)}
        if len(keys) != 2 or keys & seen:
            continue
        seen.update(keys)
        (held if fingerprint(human)[0] % 5 == 0 else pairs).append((human, generated))
    return pairs, held, seen


def evaluate(hc3, papers, abstracts, archive):
    abstracts = list(source_texts(abstracts))
    for lang in ("en", "zh"):
        pairs, held, seen = training_split(hc3, lang)
        score = fit(pairs, lang)
        calibration = [[h for h, _ in held], [p["h"][lang] for p in papers]]
        if lang == "en":
            calibration.extend(documents(archive, name) for name in
                               ("HewlettStudentEssay_real_88", "HewlettStudentEssay_GPTsimplify_88"))
        calibration_keys = {fingerprint(text) for group in calibration for text in group}
        threshold = max(cutoff([score(text) for text in group]) for group in calibration)
        print(lang, "training pairs", len(pairs), "calibration sizes", [len(c) for c in calibration])
        cohorts = {}
        paired_scores = {}
        for accession, language, role, text in abstracts:
            if language != lang:
                continue
            cohorts.setdefault(lang+" "+role, []).append(text)
            paired_scores.setdefault(accession, {})["generated" if role == "generated" else "published"] = score(text)
        gains = [row["generated"] - row["published"] for row in paired_scores.values()]
        print(lang, "same-topic generated higher / tied / lower",
              sum(d>0 for d in gains), sum(d==0 for d in gains), sum(d<0 for d in gains))
        if lang == "en":
            for name in ("TOEFL_real_91", "TOEFL_gpt4polished_91", "CS224N_real_145", "CS224N_gpt3_145",
                         "CollegeEssay_real_70", "CollegeEssay_gpt3_31"):
                cohorts[name] = documents(archive, name)
        for name, texts in cohorts.items():
            assert not ({fingerprint(t) for t in texts} & (seen | calibration_keys)), name
            flags = sum(score(t)>threshold for t in texts)
            print(name, "AI", flags, "abstain", len(texts)-flags, "N", len(texts))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("hc3", "papers", "abstracts", "archive"):
        parser.add_argument(key, type=Path)
    args = parser.parse_args()
    with ZipFile(args.archive) as archive:
        evaluate(args.hc3, json.loads(args.papers.read_bytes()), args.abstracts, archive)
