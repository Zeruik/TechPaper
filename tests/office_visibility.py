"""Check visible, deleted, moved and explicitly hidden DOCX text and drawing content."""
import argparse
from pathlib import Path


def make(folder, poster):
    from docx import Document
    from docx.oxml import OxmlElement
    from docx.shared import Inches
    folder.mkdir(parents=True, exist_ok=True)
    document = Document()
    document.add_paragraph("Public heading.")
    for mode in ("visible", "hidden", "del", "moveFrom", "paragraph-del"):
        paragraph = document.add_paragraph()
        run = paragraph.add_run("PUBLIC_VALUE" if mode == "visible" else "EXCLUDED_VALUE")
        run.add_picture(str(poster), width=Inches(2))
        if mode == "hidden":
            run.font.hidden = True
        elif mode == "visible":
            run.font.hidden = False
        else:
            target = paragraph._p if mode == "paragraph-del" else run._r
            revision = OxmlElement("w:" + ("del" if mode == "paragraph-del" else mode))
            target.addprevious(revision)
            revision.append(target)
    from docx.enum.style import WD_STYLE_TYPE
    hidden_style = document.styles.add_style("Hidden QA", WD_STYLE_TYPE.CHARACTER)
    hidden_style.font.hidden = True
    inherited = document.styles.add_style("Derived QA", WD_STYLE_TYPE.CHARACTER)
    inherited.base_style = hidden_style
    para_style = document.styles.add_style("Paragraph QA", WD_STYLE_TYPE.PARAGRAPH)
    para_style.font.hidden = True
    paragraph = document.add_paragraph(style=para_style)
    paragraph.add_run("EXCLUDED_VALUE")
    paragraph.add_run("Visible toggle", style=inherited)
    paragraph.add_run(" and visible override.").font.hidden = False
    for container in (document.add_table(rows=1, cols=1).cell(0,0), document.sections[0].header):
        run = container.paragraphs[0].add_run("EXCLUDED_VALUE", style=inherited)
        run.add_picture(str(poster), width=Inches(2))
    document.save(folder / "visibility.docx")


def verify(report):
    from lxml import html
    tree = html.fromstring(report.read_text(encoding="utf8"))
    rows = tree.xpath("//article[.//mark]")
    assert [a.find(".//mark").text for a in rows[:2]] == ["Public heading.", "PUBLIC_VALUE"]
    assert "EXCLUDED_VALUE" not in tree.text_content()
    assert len(rows) == 8
    assert rows[-1].find(".//mark").text == "Visible toggle and visible override."
    assert all("段 2 / 图 1" in a.find("small").text for a in rows[2:-1])
    print("PASS visible DOCX text and drawing retained once; hidden and revision variants excluded")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--make", type=Path)
    parser.add_argument("--poster", type=Path)
    parser.add_argument("--verify", type=Path)
    args = parser.parse_args()
    if args.make:
        if not args.poster:
            parser.error("--make requires --poster from visible_ocr.py")
        make(args.make, args.poster)
    if args.verify:
        verify(args.verify)
