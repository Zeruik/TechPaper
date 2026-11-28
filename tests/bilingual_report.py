"""Check a packaged worker's HTML highlights on the frozen bilingual cohorts."""
import argparse
from collections import Counter, defaultdict
from html.parser import HTMLParser
from pathlib import Path
import re
import subprocess
import sys
from tempfile import TemporaryDirectory
from xml.etree import ElementTree as ET
from zipfile import ZipFile

from bilingual_holdout import source_texts


class Highlights(HTMLParser):
    def __init__(self):
        super().__init__()
        self.rows = []
        self.field = None

    def handle_starttag(self, tag, attrs):
        if tag == "article":
            self.location = self.text = ""
        elif tag == "small":
            self.field = "location"
        elif tag == "mark":
            self.field = "text"
            self.label = dict(attrs)["class"]

    def handle_data(self, data):
        if self.field == "location":
            self.location += data
        elif self.field == "text":
            self.text += data

    def handle_endtag(self, tag):
        if tag in ("mark", "small"):
            self.field = None
        if tag == "mark":
            self.rows.append((self.location, self.text, self.label))


def run(folder, worker):
    rows = list(source_texts(folder))
    # One paragraph per abstract. Keep each paper, its translation and controls paired.
    w = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
    root = ET.Element(w + "document")
    body = ET.SubElement(root, w + "body")
    for _, _, _, text in rows:
        paragraph = ET.SubElement(body, w + "p")
        for i, line in enumerate(text.split("\n")):
            run_node = ET.SubElement(paragraph, w + "r")
            if i:
                ET.SubElement(run_node, w + "br")
            ET.SubElement(run_node, w + "t").text = line
    with TemporaryDirectory() as tmp:
        source, report = (Path(tmp) / name for name in ("abstracts.docx", "report.html"))
        with ZipFile(source, "w") as doc:
            doc.writestr("word/document.xml", ET.tostring(root, encoding="utf-8"))
            doc.writestr("[Content_Types].xml",
                         '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                         '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                         '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
            doc.writestr("_rels/.rels",
                         '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                         '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        command = [sys.executable, worker] if worker.suffix == ".py" else [worker]
        subprocess.run([*command, source, report], check=True, stdout=subprocess.DEVNULL)
        highlights = Highlights()
        highlights.feed(report.read_text(encoding="utf-8"))
        highlights.close()
    labels = defaultdict(Counter)
    ends = defaultdict(int)
    for location, highlighted, label in highlights.rows:
        paragraph, start, end = map(int, re.search(
            r"document\.xml / 段 (\d+) · 字符 (\d+)–(\d+)", location).groups())
        text = rows[paragraph - 1][3]
        assert 0 <= ends[paragraph] <= start < end <= len(text)
        assert not text[ends[paragraph]:start].strip()
        assert text[start:end] == highlighted
        ends[paragraph] = end
        labels[paragraph][label] += 1
    groups = defaultdict(Counter)
    for i, (_, language, role, text) in enumerate(rows, 1):
        assert labels[i] and not text[ends[i]:].strip()
        counts = groups[language, role]
        counts.update(labels[i])
        counts["documents"] += 1
        if set(labels[i]) == {"未判断"}:
            counts["all_abstained"] += 1
    for group, counts in sorted(groups.items()):
        print(*group, *(counts[k] for k in ("documents", "all_abstained", "AI", "MIX", "HUMAN", "未判断")))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("worker", type=Path)
    args = parser.parse_args()
    run(args.folder, args.worker.resolve())
