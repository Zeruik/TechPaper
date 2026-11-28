"""Compare topic-paired style models on journal- and byline-disjoint academic folds."""
import argparse
from collections import Counter, defaultdict
from pathlib import Path
import json
import re

from bilingual_holdout import source_texts
from author_holdout import authors
from journal_transfer import published_abstracts
from generator_holdout import MODELS, TOPICS
from paired_transfer import independent_folds
from style_benchmark import fingerprint, fit, training_split
from title_controls import read_control


def evaluate(papers, calibration, hc3, accessions, engineering=None, extra_folder=None, extra_ids=(), title_controls=None):
    rows = list(source_texts(papers))
    folds = independent_folds(papers)
    controls = list(published_abstracts(calibration, papers, accessions))
    if bool(extra_folder) != bool(extra_ids):
        raise ValueError("Supply both extra_folder and extra_ids")
    if title_controls and not extra_folder:
        raise ValueError("Title controls require the matching external papers")
    extra = []
    if extra_folder:
        calibration_authors = set().union(*(authors(calibration/(pid+".xml")) for pid in accessions))
        for pid in extra_ids:
            if authors(extra_folder/(pid+".xml")) & calibration_authors:
                raise ValueError("Extra byline overlaps calibration")
        extra = list(published_abstracts(extra_folder, papers, extra_ids))
    for lang in ("en", "zh"):
        qa, _, seen = training_split(hc3, lang)
        qa_score = fit(qa, lang)
        cutoff_texts = [text for _, language, text in controls if language == lang]
        keys = {fingerprint(text) for text in cutoff_texts}
        groups = defaultdict(dict)
        for paper, language, role, text in rows:
            if language == lang:
                key = fingerprint(text)
                if key in keys or key in seen:
                    raise ValueError("Repeated text across cohorts")
                keys.add(key)
                groups[paper][role] = text
        if {fingerprint(t) for t in cutoff_texts} & seen or not cutoff_texts:
            raise ValueError("Calibration overlap or missing controls")
        counts = defaultdict(Counter)
        academic_models = []
        for test in folds:
            pairs = [(next(t for r,t in g.items() if r != "generated"), g["generated"])
                     for paper,g in groups.items() if paper not in test]
            for variant, score in (("QA", qa_score), ("academic", fit(pairs, lang))):
                threshold = max(score(t) for t in cutoff_texts)
                if variant == "academic":
                    academic_models.append((score, threshold))
                for paper in test:
                    for role, text in groups[paper].items():
                        counts[variant, role]["AI" if score(text)>threshold else "abstain"] += 1
        for (variant, role), group in sorted(counts.items()):
            print(lang, variant, role, "AI / abstain", group["AI"], group["abstain"])
        if extra:
            votes = Counter()
            qa_cutoff = max(qa_score(t) for t in cutoff_texts)
            qa_alerts = 0
            generated = Counter()
            for paper, language, text in extra:
                if lang != language:
                    continue
                key = fingerprint(text)
                if key in keys or key in seen:
                    raise ValueError("Repeated external test text")
                keys.add(key)
                qa_alerts += qa_score(text) > qa_cutoff
                votes[sum(score(text)>threshold for score,threshold in academic_models)] += 1
                if title_controls:
                    draft=read_control(title_controls,paper,lang)
                    if not draft or fingerprint(draft) in keys or fingerprint(draft) in seen:
                        raise ValueError("Empty or duplicate title control")
                    keys.add(fingerprint(draft))
                    generated["QA"] += qa_score(draft)>qa_cutoff
                    generated["academic"] += all(score(draft)>threshold for score,threshold in academic_models)
                    generated["N"] += 1
                    units = r"[\u3400-\u9fff]" if lang=="zh" else r"\S+"
                    ends = [[m.end() for m in re.finditer(units,s)] for s in (text,draft)]
                    length = min(map(len,ends))
                    if not length:
                        raise ValueError("Empty length control")
                    for role,s,offsets in zip(("published","generated"),(text,draft),ends):
                        excerpt=s[:offsets[length-1]]
                        generated[role+"Prefix"] += all(score(excerpt)>threshold for score,threshold in academic_models)
            print(lang,"external published QA AI / abstain",qa_alerts,sum(votes.values())-qa_alerts)
            print(lang,"external published unanimous AI / abstain",votes[len(folds)],sum(votes.values())-votes[len(folds)])
            print(lang,"external number of academic folds flagging each abstract",sorted(votes.items()))
            if title_controls:
                print(lang,"same-title generated QA / academic / total",generated["QA"],generated["academic"],generated["N"])
                print(lang,"matched prefix academic published / generated / pairs",
                      generated["publishedPrefix"],generated["generatedPrefix"],generated["N"])
        if engineering:
            for name,digest in MODELS.items():
                flagged = 0
                for i in range(len(TOPICS)):
                    row = json.loads((engineering/f"{digest[:12]}-{i}-{lang}.json").read_bytes())
                    if (row["model"],row["digest"],row["language"],row["topic"],row["finished"]) != (name,digest,lang,i,"stop"):
                        raise ValueError("Engineering control mismatch")
                    text = row["text"].strip()
                    if not text or fingerprint(text) in keys | seen:
                        raise ValueError("Empty or duplicate engineering control")
                    keys.add(fingerprint(text))
                    flagged += all(score(text)>threshold for score,threshold in academic_models)
                print(lang, name, "academic unanimous AI / abstain", flagged, len(TOPICS)-flagged)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("papers","calibration","hc3"):
        parser.add_argument(name,type=Path)
    parser.add_argument("accessions",nargs="+")
    parser.add_argument("--engineering",type=Path)
    parser.add_argument("--extra-folder",type=Path)
    parser.add_argument("--extra-ids",nargs="+")
    parser.add_argument("--title-controls",type=Path)
    evaluate(**vars(parser.parse_args()))
