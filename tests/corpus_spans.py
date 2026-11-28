"""Check exact local-source ranges against a brute-force oracle and exported HTML."""
from html import escape
from pathlib import Path
import random
import sys
from tempfile import TemporaryDirectory
from xml.etree import ElementTree as ET
from zipfile import ZipFile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
from review import Corpus, W, report


def reference(source, text):
    i = 0
    while i <= len(text) - Corpus.width:
        candidates = [j for j in range(len(source)) if source.startswith(text[i:i+Corpus.width], j)]
        if not candidates:
            i += 1
            continue
        best = 0
        for j in candidates:
            length = Corpus.width
            while i+length < len(text) and j+length < len(source) and text[i+length] == source[j+length]:
                length += 1
            if length > best:
                best, start = length, j
        yield i, start, best
        i += best


def check(folder):
    path = folder / "中文 语料.txt"
    prefix = "实验记录使用同一套室温采样程序逐次测量"
    full = prefix + "并复核仪表读数"
    other = "A separate <reference> & its values"
    source = prefix + "。\r\n" + full + "\r\n" + full + "\r\n" + other
    path.write_bytes(source.encode("utf-8-sig"))
    corpus = Corpus(path)
    text = "导语。" + full + "；" + other
    matches = list(corpus.spans(text))
    assert matches == [(3, source.index(full), len(full)), (4+len(full), source.index(other), len(other))]
    with ZipFile(folder / "input.docx", "w") as doc:
        root = ET.Element(W+"document")
        paragraph = ET.SubElement(ET.SubElement(root, W+"body"), W+"p")
        ET.SubElement(ET.SubElement(paragraph, W+"r"), W+"t").text = text
        doc.writestr("word/document.xml", ET.tostring(root))
    report(folder/"input.docx", folder/"report.html", path)
    html = (folder/"report.html").read_text(encoding="utf8")
    for i, j, length in matches:
        assert f"语料字符 {j}–{j+length} · 段内字符 {i}–{i+length} · {escape(source[j:j+length])}" in html
    assert list(Corpus(None).match_lines(text, 0)) == ["未选择语料"]
    assert list(corpus.match_lines(full[:15], 0)) == ["未命中连续 16 字符"]
    rng = random.Random(37)
    blocks = [prefix, full, other, "🧪"*16, "a"*40]
    for _ in range(200):
        source = "".join(rng.choices(blocks, k=6))
        query = "".join(rng.choices(blocks+["?"], k=5))
        path.write_text(source, encoding="utf8")
        assert list(Corpus(path).spans(query)) == list(reference(source, query))


if __name__ == "__main__":
    with TemporaryDirectory() as temp:
        check(Path(temp))
    print("PASS corpus ranges, HTML, and 200 seeded reference comparisons")
