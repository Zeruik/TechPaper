"""Check explicit and inherited Word outline levels in an exported report."""
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory

from docx import Document
from docx.enum.style import WD_STYLE_TYPE
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from lxml import html


def level(paragraph, value):
    tag = OxmlElement("w:outlineLvl")
    tag.set(qn("w:val"), str(value))
    paragraph._p.get_or_add_pPr().append(tag)


def main(worker):
    text = "I recorded 12 independent measurements during calibration."
    with TemporaryDirectory() as tmp:
        folder = Path(tmp)
        doc = Document()
        custom = doc.styles.add_style("Calibration section", WD_STYLE_TYPE.PARAGRAPH)
        custom.base_style = doc.styles["Heading 2"]
        doc.add_paragraph(text, style=custom)
        level(doc.add_paragraph(text), 8)
        level(doc.add_paragraph(text, style=custom), 9)
        doc.add_paragraph(text)
        doc.save(folder / "sample.docx")
        corpus = folder / "source.txt"
        corpus.write_text(text, encoding="utf8")
        output = folder / "report.html"
        subprocess.run([str(worker), str(folder/"sample.docx"), str(output), "--corpus", str(corpus)], check=True)
        tree = html.fromstring(output.read_bytes().decode("utf8"))
        rows = tree.xpath("//article[.//mark]")
        assert [r.find(".//mark").get("class") for r in rows] == ["标题","标题","HUMAN","HUMAN"]
        for i,row in enumerate(rows,1):
            assert row.find(".//mark").text == text
            assert f"段 {i} · 字符 0–{len(text)}" in row.find("small").text
            assert "source.txt" in row.find("aside").text_content()
        assert "另列大纲标题片段 2" in tree.text_content()
        assert "共 2 个非空片段，分类 2（100.0%）" in tree.text_content()
        headings = Document()
        headings.add_heading(text, level=1)
        headings.save(folder / "headings.docx")
        output = folder / "headings.html"
        subprocess.run([str(worker), str(folder/"headings.docx"), str(output)], check=True)
        assert "无可分类正文，覆盖率不适用" in output.read_text(encoding="utf8")
        print("PASS inherited/direct headings, body override, anchors, corpus and coverage")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
