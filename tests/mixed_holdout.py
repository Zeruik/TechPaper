"""Check within-paragraph provenance and exact offsets in reports from an installed worker."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import re
import subprocess
import tempfile

from docx import Document
from lxml import html

from bilingual_holdout import source_texts


def benchmark(folder, worker, segment_output, context_output):
    by_source = defaultdict(list)
    for accession, lang, role, text in source_texts(folder):
        by_source[accession, lang].append((role, text))

    document = Document()
    paragraphs = []
    for index, ((_, lang), blocks) in enumerate(sorted(by_source.items())):
        assert len(blocks) == 2 and blocks[-1][0] == "generated"
        if index // 2 % 2:
            blocks.reverse()
        text = "\n".join(value for _, value in blocks)
        document.add_paragraph(text)
        paragraphs.append((lang, blocks, text))

    with tempfile.TemporaryDirectory() as tmp:
        source = Path(tmp) / "mixed.docx"
        output = Path(tmp) / "mixed.html"
        document.save(source)
        subprocess.run([str(worker.resolve()), str(source), str(output)], check=True, capture_output=True)
        tree = html.fromstring(output.read_text(encoding="utf8"))

    results = Counter()
    spans = defaultdict(list)
    groups = defaultdict(list)
    ordered = defaultdict(list)
    articles = tree.xpath("//article")
    for article in articles:
        mark = article.find(".//mark")
        if mark is None:
            raise ValueError("Worker skipped an input")
        loc = article.findtext("small")
        m = re.search(r"document\.xml / 段 (\d+) · 字符 (\d+)–(\d+) ·", loc)
        if not m:
            raise ValueError("Missing DOCX source coordinates")
        n, start, end = map(int, m.groups())
        assert 1 <= n <= len(paragraphs)
        lang, blocks, text = paragraphs[n-1]
        assert 0 <= start < end <= len(text)
        assert text[start:end] == mark.text and article.get("id") == f"p{n}s{start}"
        split = len(blocks[0][1])
        if start < split < end:
            raise ValueError("Highlight crosses known source boundary")
        role = blocks[int(start > split)][0]
        label = mark.get("class")
        assert label in ("AI", "MIX", "HUMAN", "未判断")
        results[lang, role, label] += 1
        spans[n].append((start, end))
        groups[f"{lang} {role}"].append(mark.text)
        ordered[n].append((f"{lang} {role}", start, end))

    for n, (_, _, text) in enumerate(paragraphs, 1):
        offset = 0
        for start, end in spans[n]:
            assert offset <= start and not text[offset:start].strip()
            offset = end
        assert not text[offset:].strip()
    for name, cases in sorted(groups.items()):
        lang, role = name.split()
        print(name, len(cases), *(results[lang, role, label] for label in ("AI", "MIX", "HUMAN", "未判断")))
    if segment_output:
        segment_output.write_text(json.dumps(groups, ensure_ascii=False), encoding="utf8")
    if context_output:
        contexts = defaultdict(list)
        for n, rows in ordered.items():
            text = paragraphs[n-1][2]
            for i, (cohort, _, _) in enumerate(rows):
                start = rows[max(0, i-1)][1]
                end = rows[min(len(rows)-1, i+1)][2]
                contexts[cohort].append(text[start:end])
        context_output.write_text(json.dumps(contexts, ensure_ascii=False), encoding="utf8")



if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("worker", type=Path)
    parser.add_argument("--segments", type=Path, help="Optional local-only ONNX benchmark input containing source text")
    parser.add_argument("--contexts", type=Path, help="Also output each center span with adjacent spans in its paragraph")
    args = parser.parse_args()
    benchmark(args.folder, args.worker, args.segments, args.contexts)
