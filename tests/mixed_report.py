"""Audit source locations and writing signals on synthetic, within-topic mixed paragraphs."""
import argparse
from collections import Counter, defaultdict
from pathlib import Path
import re

from bilingual_holdout import source_texts
from bilingual_report import report_rows


def sentences(text):
    # End at full sentence punctuation, keeping decimal points within measurements.
    result = []
    for line in text.splitlines():
        for match in re.finditer(r".+?(?:[。！？!?]+|\.(?=\s|$))", line):
            sentence = match.group().strip()
            if len(sentence) >= 30 and sentence not in result:
                result.append(sentence)
    return result


def evaluate(folder, worker, model=None):
    if model:
        from journal_transfer import decisions, load_candidate
        session, tokenizer = load_candidate(model)
    sources = defaultdict(dict)
    for paper, lang, role, text in source_texts(folder):
        sources[paper, lang][role] = sentences(text)
    rows, origins = [], []
    paper_order = sorted({paper for paper, _ in sources})
    for (paper, lang), candidates in sorted(sources.items()):
        published, = candidates.keys() - {"generated"}
        first, second = candidates[published], candidates["generated"]
        pair_count = min(2, len(first), len(second))
        if not pair_count:
            raise ValueError(f"Missing complete sentence for {paper} {lang}")
        # Reverse the starting source on alternate papers within each language.
        pairs = [(published, first), ("generated", second)]
        if paper_order.index(paper) % 2:
            pairs.reverse()
        text = ""
        boundaries = []
        for i in range(pair_count):
            for role, items in pairs:
                if text:
                    text += " "
                start = len(text)
                text += items[i]
                boundaries.append((start, len(text), role))
        rows.append((paper, lang, "mixed", text))
        origins.append(boundaries)
    _, highlights = report_rows(rows, worker)
    counts = defaultdict(Counter)
    for index, ((_, lang, _, text), boundaries) in enumerate(zip(rows, origins), 1):
        votes = defaultdict(set)
        candidate_votes = defaultdict(set)
        for start, end, label in highlights[index]:
            while start < end and text[start].isspace():
                start += 1
            while start < end and text[end - 1].isspace():
                end -= 1
            owner, = [(a, b, role) for a, b, role in boundaries if a <= start < end <= b]
            votes[owner].add(label)
            if model:
                for variant, prediction in decisions(session, tokenizer, text[start:end], lang):
                    candidate_votes[variant, owner].add(prediction)
        for (variant, owner), labels in candidate_votes.items():
            counts["Q4-"+variant, lang, owner[2]][next(iter(labels)) if len(labels)==1 else "multiple"] += 1
        for owner in boundaries:
            labels = votes[owner]
            if not labels:
                raise ValueError("Source sentence missing from report")
            counts["baseline", lang, owner[2]][next(iter(labels)) if len(labels)==1 else "multiple"] += 1
    for (engine, lang, role), group in sorted(counts.items()):
        print(engine, lang, role, "source sentences", sum(group.values()), "AI MIX HUMAN abstain multiple",
              group["AI"], group["MIX"], group["HUMAN"], group["未判断"]+group["abstain"], group["multiple"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("worker", type=Path)
    parser.add_argument("--model", type=Path, help="Compare the frozen Q4 candidate on the same extracted spans")
    args = parser.parse_args()
    evaluate(args.folder, args.worker.resolve(), args.model)
