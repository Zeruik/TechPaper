"""Verify presentation visibility flags without distributing any slide or image."""
import argparse
from pathlib import Path


def make(path, poster):
    from pptx import Presentation
    from pptx.util import Inches
    deck = Presentation()
    slide = deck.slides.add_slide(deck.slide_layouts[6])

    def textbox(shapes, text):
        shape = shapes.add_textbox(0, 0, Inches(4), Inches(1))
        shape.text = text
        return shape

    def hidden(shape, value="1"):
        shape._element.xpath("./*/p:cNvPr")[0].set("hidden", value)

    hidden(textbox(slide.shapes, "HIDDEN_SHAPE"))
    group = slide.shapes.add_group_shape()
    textbox(group.shapes, "HIDDEN_CHILD")
    group.shapes.add_picture(str(poster), 0, 0, Inches(2))
    hidden(group, "true")
    hidden(slide.shapes.add_picture(str(poster), 0, 0, Inches(2)))
    hidden(textbox(slide.shapes, "Visible heading."), "false")
    slide.shapes.add_picture(str(poster), 0, Inches(1), Inches(6))
    omitted = deck.slides.add_slide(deck.slide_layouts[6])
    omitted._element.set("show", "0")
    textbox(omitted.shapes, "HIDDEN_SLIDE")
    omitted.shapes.add_picture(str(poster), 0, 0, Inches(2))
    last = deck.slides.add_slide(deck.slide_layouts[6])
    last._element.set("show", "1")
    textbox(last.shapes, "Last visible slide.")
    deck.save(path)


def verify(report):
    from bilingual_report import Highlights
    html = report.read_text(encoding="utf-8")
    parser = Highlights()
    parser.feed(html)
    parser.close()
    assert "HIDDEN_" not in html
    assert html.count("显式隐藏的幻灯片对象已跳过")==3
    assert "幻灯片 2 · 放映时隐藏的幻灯片已跳过" in html
    assert len(parser.rows)==7
    locations = [row[0] for row in parser.rows]
    assert "幻灯片 1 / 段 3" in locations[0] and parser.rows[0][1]=="Visible heading."
    assert "幻灯片 3 / 段 1" in locations[-1] and parser.rows[-1][1]=="Last visible slide."
    lines = ["".join(row[1].split()) for row in parser.rows[1:-1]]
    assert sorted(lines)==sorted(["样件A|SampleA", "室温22.5°C", "零点记录", "Tolerance0.25mm", "零点记录"])
    assert all("幻灯片 1 / 图 3" in loc and " / px " in loc for loc in locations[1:-1])
    print("PASS visible PPTX paragraphs and image, hidden shapes, inherited group and hidden slide")


if __name__=="__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--make", nargs=2, type=Path, metavar=("PPTX", "POSTER"))
    modes.add_argument("--verify", type=Path)
    args = parser.parse_args()
    make(*args.make) if args.make else verify(args.verify)
