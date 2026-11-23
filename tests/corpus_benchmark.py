"""Offline research benchmark. Input texts and trained parameters stay local."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import re
from zipfile import ZipFile


def features(text, language):
    text = re.sub(r"\s+", " ", text.lower()).strip()
    tokens = re.findall(r"[a-z]+|[0-9]+|[^\w\s]", text)
    if language == "zh":
        return {text[i:i+2] for i in range(len(text)-1)}
    return set(tokens) | {" ".join(pair) for pair in zip(tokens, tokens[1:])}


def training_rows(folder, language):
    seen_questions = set()
    for path in sorted(folder.glob(language + "-*.json")):
        for item in json.loads(path.read_bytes())["rows"]:
            row = item["row"]
            question = row["question"].strip()
            if question in seen_questions or not row["human_answers"] or not row["chatgpt_answers"]:
                continue
            if not all(isinstance(x, str) and x.strip() for x in (row["human_answers"][0],row["chatgpt_answers"][0])):
                continue
            seen_questions.add(question)
            yield row["human_answers"][0], row["chatgpt_answers"][0]


def fit(pairs, language):
    human, generated = Counter(), Counter()
    for h, g in pairs:
        human.update(features(h, language))
        generated.update(features(g, language))
    # Use document frequency and symmetric smoothing to limit rare vocabulary.
    weights = {t: math.log((generated[t]+2)/(human[t]+2))
               for t in human.keys() | generated.keys() if human[t]+generated[t]>=5}
    def score(text):
        values = [weights[t] for t in sorted(features(text, language)) if t in weights]
        return sum(values)/max(1,len(values))
    return score


def documents(archive, name):
    entry, = [n for n in archive.namelist() if n.endswith("/"+name+"/data.json")]
    return [r["document"] for r in json.loads(archive.read(entry))]


def benchmark(args):
    papers = json.loads(args.papers.read_text(encoding="utf-8-sig"))
    with ZipFile(args.archive) as archive:
        for lang in ("en","zh"):
            pairs = list(training_rows(args.hc3,lang))
            score = fit(pairs,lang)
            # Keep full source cohorts together. Never choose a threshold on test labels.
            human_cal = [p["h"][lang] for p in papers[:2]]
            if lang=="en":
                for cohort in ("HewlettStudentEssay_real_88","HewlettStudentEssay_GPTsimplify_88"):
                    human_cal.extend(documents(archive,cohort))
            threshold = max(score(t) for t in human_cal) + 1e-9
            print(lang, "training questions", len(pairs), "calibration originals/variants", len(human_cal))
            def report(name,texts):
                flags=sum(score(t)>=threshold for t in texts)
                print(name,flags,"/",len(texts),"flagged,",len(texts)-flags,"abstentions")
            for key in ("h","g"):
                report("academic "+key,[p[key][lang] for p in papers[2:]])
            if lang=="en":
                for cohort in ("CS224N_real_145","CS224N_gpt3_145","CS224N_gpt3PromptEng_145",
                               "TOEFL_real_91","TOEFL_gpt4polished_91"):
                    report(cohort,documents(archive,cohort))
            else:
                for side in ("before","after"):
                    report(side,[(args.example/(side+".txt")).read_text(encoding="utf8")])

if __name__=="__main__":
    parser=argparse.ArgumentParser()
    for name in ("hc3","archive","papers","example"):
        parser.add_argument("--"+name,type=Path,required=True)
    benchmark(parser.parse_args())
