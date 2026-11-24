"""Create a mixed PDF with a reusable nested image and verify report text and page anchors."""
import argparse
from pathlib import Path
import re


def make(folder):
    import pymupdf
    folder.mkdir(parents=True, exist_ok=True)
    with pymupdf.open() as artwork, pymupdf.open() as form, pymupdf.open() as output:
        tile = artwork.new_page(width=200, height=60)
        tile.insert_text((10, 35), "Tolerance 0.25 mm", fontsize=20)
        bitmap = tile.get_pixmap(matrix=pymupdf.Matrix(2, 2)).tobytes("png")
        form.new_page(width=200, height=60).insert_image(tile.rect, stream=bitmap)
        page = output.new_page(width=480, height=480)
        page.insert_text((24, 28), "Table A", fontsize=18)
        for x, y in ((30, 75), (210, 270)):
            page.draw_rect(pymupdf.Rect(x-3, y-3, x+203, y+63))
            page.show_pdf_page(pymupdf.Rect(x, y, x+200, y+60), form, 0)
        output.save(folder / "mixed-table.pdf")


def verify(report):
    from lxml import html
    tree = html.fromstring(report.read_bytes().decode("utf8"))
    entries = tree.xpath("//article[.//mark]")
    assert entries and entries[0].find(".//mark").text == "Table A"
    cell_text = [[], []]
    for entry in entries[1:]:
        loc = entry.find("small").text
        assert "第 1 页" in loc and " / px " in loc
        quad = list(map(int, re.findall(r"-?\d+", loc.split(" / px ")[1].split(" ·")[0])))
        assert len(quad) == 8
        containing = [i for i, (x, y) in enumerate(((30, 75), (210, 270)))
                      if all(x < u/2 < x+200 and y+8 < v/2 < y+50 for u,v in zip(quad[::2], quad[1::2]))]
        assert len(containing) == 1, quad
        cell_text[containing[0]].append(entry.find(".//mark").text)
    for tokens in cell_text:
        assert "".join("".join(tokens).split()) == "Tolerance0.25mm", tokens
    print("PASS mixed table: native heading, two nested image instances, page pixel anchors")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--make", type=Path)
    parser.add_argument("--verify", type=Path)
    args = parser.parse_args()
    if args.make:
        make(args.make)
    if args.verify:
        verify(args.verify)
