"""Verify original-text and OCR order across a table containing an inline image."""
import argparse
from pathlib import Path


def make(source, poster, image_name=None):
    from docx import Document
    from docx.shared import Inches
    document = Document()
    document.add_paragraph("Before table.")
    cells = document.add_table(rows=1, cols=2).rows[0].cells
    paragraph = cells[0].paragraphs[0]
    paragraph.add_run("Before figure.")
    paragraph.add_run().add_picture(str(poster), width=Inches(2))
    paragraph.add_run("After figure.")
    cells[1].text = "Second cell."
    document.add_paragraph("After table.")
    if image_name is None:
        document.save(source)
        return
    from io import BytesIO
    from urllib.parse import quote
    from xml.etree import ElementTree as ET
    from zipfile import ZipFile
    original = BytesIO()
    document.save(original)
    picture = "docProps/合成 展板.png"
    with ZipFile(original) as src, ZipFile(source, "x") as dest:
        image, = [name for name in src.namelist() if name.startswith("word/media/")]
        for name in src.namelist():
            if name == "docProps/thumbnail.jpeg":
                continue
            data = src.read(name)
            if name == image:
                name = quote(picture) if image_name == "encoded" else picture
            if name in ("_rels/.rels", "word/_rels/document.xml.rels"):
                root = ET.fromstring(data)
                for node in root:
                    if node.get("Type").endswith("/thumbnail"):
                        node.set("Target", quote(picture))
                    elif node.get("Type").endswith("/image"):
                        node.set("Target", "../"+quote(picture))
                data = ET.tostring(root, encoding="utf-8")
            dest.writestr(name, data)


def verify(report):
    from lxml import html
    tree = html.fromstring(report.read_text(encoding="utf8"))
    rows = tree.xpath("//article[.//mark]")
    expected = ["Before table.", "Before figure.", "样件 A | Sample A", "室温 22.5 °C",
                "零点记录", "Tolerance 0.25 mm", "零点记录", "After figure.", "Second cell.", "After table."]
    assert ["".join(r.find(".//mark").text.split()) for r in rows] == ["".join(s.split()) for s in expected]
    assert [r.find("small").text.split(" · ")[0] for r in (rows[0], rows[-2], rows[-1])] == [
        f"document.xml / 段 {i}" for i in (1, 3, 4)]
    for index, row in enumerate(rows[2:7], 1):
        assert f"段 2 / 图 1（原图像素） / 行 {index} / px " in row.find("small").text
    for index, row in enumerate((rows[1], rows[7]), 1):
        assert f"段 2 / 文字片段 {index} · 字符 0–" in row.find("small").text
    assert len({r.get("id") for r in rows}) == len(rows)
    print("PASS DOCX table and inline image order, duplicate lines, numeric units and source anchors")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--make", type=Path)
    modes.add_argument("--verify", type=Path)
    parser.add_argument("--poster", type=Path)
    parser.add_argument("--image-name", choices=("literal","encoded"), help="Use shared picture and encoded relationship target")
    args = parser.parse_args()
    if args.make:
        if not args.poster:
            parser.error("Supply the synthetic visible_ocr.py poster with --poster")
        make(args.make, args.poster, args.image_name)
    else:
        verify(args.verify)
