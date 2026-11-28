"""Frozen multilingual candidate on a separate journal's published bilingual abstracts."""
import argparse
from collections import Counter
import json
from pathlib import Path
from xml.etree import ElementTree as ET

import onnxruntime as ort
from tokenizers import Tokenizer

from author_holdout import authors
from bilingual_holdout import ARTICLES, LANG
from calibration_benchmark import document_scores
from generator_holdout import MODELS, TOPICS
from multilingual_transfer import DIGESTS, verify_assets


def decisions(session, tokenizer, text, lang):
    low, high = document_scores(session,tokenizer,text,1,max_length=256)
    # English B is frozen by the earlier human translation calibration.
    limits = [("A", .95)] + ([("B", .9980590613029607)] if lang=="en" else [])
    for variant, threshold in limits:
        yield variant, "AI" if low>threshold else "HUMAN" if high<=.05 else "abstain"


def main(folder, previous, model, accessions, prior_accessions=(), controls=None):
    occupied = set().union(*(authors(previous / (paper + ".xml")) for paper in ARTICLES))
    for paper in prior_accessions:
        occupied.update(authors(folder / (paper + ".xml")))
    verify_assets(model, DIGESTS)
    ort.disable_telemetry_events()
    options = ort.SessionOptions()
    options.intra_op_num_threads = 3
    session = ort.InferenceSession(str(model/"model_q4.onnx"), options, providers=["CPUExecutionProvider"])
    tokenizer = Tokenizer.from_file(str(model/"tokenizer.json"))
    tokenizer.no_padding()
    tokenizer.no_truncation()
    counts = Counter()
    for accession in accessions:
        path = folder / (accession + ".xml")
        root = ET.parse(path).getroot()
        meta = root.find("./front/article-meta")
        ids = {e.text for e in meta.findall("article-id") if e.get("pub-id-type") in ("pmc","pmcid")}
        if accession not in ids and accession.removeprefix("PMC") not in ids:
            raise ValueError("Source identifier mismatch")
        licence = meta.find("permissions/license")
        if licence is None or "CC BY 4.0" not in "".join(licence.itertext()):
            raise ValueError("Unverified licence")
        identities = authors(path)
        if identities & occupied:
            raise ValueError("Overlapping bylines")
        occupied.update(identities)
        for lang, tag in (("zh","abstract"), ("en","trans-abstract")):
            node = meta.find(tag)
            if node is None or node.get(LANG,root.get(LANG)) != lang:
                raise ValueError("Unverified language or role")
            text = "\n".join(" ".join("".join(p.itertext()).split()) for p in node.iter("p"))
            if not text.strip():
                raise ValueError("Empty abstract")
            for variant, label in decisions(session, tokenizer, text, lang):
                counts[lang,variant,label] += 1
    for lang,variant in (("zh","A"),("en","A"),("en","B")):
        print(lang,variant,"N",len(accessions),"AI HUMAN abstain",*(counts[lang,variant,k] for k in ("AI","HUMAN","abstain")))
    if controls is not None:
        for name, digest in MODELS.items():
            counts = Counter()
            for topic in range(len(TOPICS)):
                for lang in ("zh", "en"):
                    row = json.loads((controls / f"{digest[:12]}-{topic}-{lang}.json").read_bytes())
                    if (row["model"], row["digest"], row["topic"], row["language"], row["finished"]) != (
                            name, digest, topic, lang, "stop") or not row["text"].strip():
                        raise ValueError("Incomplete or mismatched generated control")
                    for variant, label in decisions(session, tokenizer, row["text"].strip(), lang):
                        counts[lang, variant, label] += 1
            for lang, variant in (("zh","A"),("en","A"),("en","B")):
                print(name, lang, variant, "AI HUMAN abstain",
                      *(counts[lang,variant,k] for k in ("AI","HUMAN","abstain")))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("folder","previous","model"):
        parser.add_argument(name,type=Path)
    parser.add_argument("accessions",nargs="+")
    parser.add_argument("--controls", type=Path, help="Score existing paired local engineering controls")
    parser.add_argument("--prior-accessions", nargs="+", default=(),
                        help="Also exclude bylines from previously tested articles in folder")
    main(**vars(parser.parse_args()))
