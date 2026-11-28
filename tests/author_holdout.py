"""Cluster published abstracts by shared JATS bylines before evaluating local models."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import unicodedata
from xml.etree import ElementTree

import onnxruntime as ort
from tokenizers import Tokenizer

from bilingual_holdout import ARTICLES, source_texts
from calibration_benchmark import document_scores


def normalized(value):
    return "".join(c for c in unicodedata.normalize("NFKC", value).casefold() if c.isalnum())


def authors(path):
    root = ElementTree.parse(path)
    identities = set()
    contributors = [person for group in root.findall("./front/article-meta/contrib-group")
                    for person in group.findall("contrib")
                    if person.get("contrib-type", group.get("content-type")) == "author"]
    if not contributors:
        raise ValueError("Missing byline in " + path.stem)
    for contributor in contributors:
        aliases = set()
        for node in contributor.findall("name") + contributor.findall("name-alternatives/name"):
            surname = normalized(node.findtext("surname", ""))
            given = normalized(node.findtext("given-names", ""))
            if surname and given:
                aliases.add((surname, given))
        for node in contributor.findall("contrib-id[@contrib-id-type='orcid']"):
            if node.text:
                aliases.add(("orcid", node.text.rstrip("/").rsplit("/", 1)[-1].upper()))
        if not aliases:
            raise ValueError("Unidentified contributor in " + path.stem)
        identities.update(aliases)
    return identities


def components(byline):
    remaining = set(byline)
    groups = []
    while remaining:
        first = min(remaining)
        remaining.remove(first)
        component, pending = {first}, [first]
        while pending:
            current = pending.pop()
            linked = {p for p in remaining if byline[p] & byline[current]}
            component.update(linked)
            pending.extend(linked)
            remaining.difference_update(linked)
        groups.append(sorted(component))
    return groups


def evaluate(folder, models):
    byline = {p:authors(folder / (p+".xml")) for p in ARTICLES}
    by_source = {}
    for p in ARTICLES:
        journal = ElementTree.parse(folder/(p+".xml")).findtext("./front/journal-meta/journal-title-group/journal-title")
        if not journal or not normalized(journal):
            raise ValueError("Missing journal in "+p)
        by_source[p] = byline[p] | {("journal", normalized(journal))}
    partitions = {"byline":components(byline), "journal_and_byline":components(by_source)}
    for name, groups in partitions.items():
        print(name, groups)
    texts = list(source_texts(folder))
    for lang, model in models.items():
        labels = json.loads((model.parent/"config.json").read_bytes())["id2label"]
        if set(labels.values()) != {"ChatGPT", "Human"}:
            raise ValueError("Unexpected label mapping")
        ai = next(int(k) for k, v in labels.items() if v == "ChatGPT")
        tok = Tokenizer.from_file(str(model.parent/"tokenizer.json"))
        tok.no_truncation()
        tok.no_padding()
        options = ort.SessionOptions()
        options.intra_op_num_threads = 3
        session = ort.InferenceSession(str(model), options, providers=["CPUExecutionProvider"])
        scores = {(paper,role):document_scores(session,tok,text,ai)
                  for paper,language,role,text in texts if language==lang}
        for name, groups in partitions.items():
            for adapt in (False, True):
                counts = defaultdict(Counter)
                recall = []
                for test in groups:
                    controls = [lo for (p,role),(lo,_) in scores.items() if p not in test and role!="generated"]
                    limit = max([.95]+controls) if adapt else .95
                    hits = 0
                    for (p, role), (lo, hi) in scores.items():
                        if p not in test:
                            continue
                        decision = "AI" if lo>limit else "HUMAN" if hi<=.05 else "abstain"
                        if role=="generated" and decision=="AI":
                            hits += 1
                        if p==test[0]:
                            counts[role][decision] += 1
                    recall.append(hits/len(test))
                print(lang, name, "calibrated" if adapt else "fixed", "mean cluster recall", sum(recall)/len(recall))
                for role in sorted(counts):
                    print(role, *(counts[role][k] for k in ("AI","HUMAN","abstain")))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ("folder", "english_model", "chinese_model"):
        parser.add_argument(arg, type=Path)
    args = parser.parse_args()
    evaluate(args.folder, {"en":args.english_model, "zh":args.chinese_model})
