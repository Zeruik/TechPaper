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


def verify(report, reordered=False, middle="hidden"):
    from bilingual_report import Highlights
    html = report.read_text(encoding="utf-8")
    parser = Highlights()
    parser.feed(html)
    parser.close()
    assert "HIDDEN_" not in html
    assert html.count("显式隐藏的幻灯片对象已跳过")==3
    reason={"hidden":"放映时隐藏的幻灯片已跳过","missing":"幻灯片部件缺失，已跳过","damaged":"幻灯片部件损坏，已跳过"}[middle]
    assert "幻灯片 2 · "+reason in html
    assert len(parser.rows)==7
    poster_page, text_page=(3,1) if reordered else (1,3)
    poster_rows=parser.rows[1:] if reordered else parser.rows[:-1]
    text_row=parser.rows[0] if reordered else parser.rows[-1]
    assert f"幻灯片 {poster_page} / 段 3" in poster_rows[0][0] and poster_rows[0][1]=="Visible heading."
    assert f"幻灯片 {text_page} / 段 1" in text_row[0] and text_row[1]=="Last visible slide."
    lines = ["".join(row[1].split()) for row in poster_rows[1:]]
    assert sorted(lines)==sorted(["样件A|SampleA", "室温22.5°C", "零点记录", "Tolerance0.25mm", "零点记录"])
    assert all(f"幻灯片 {poster_page} / 图 3" in row[0] and " / px " in row[0] for row in poster_rows[1:])
    print("PASS visible PPTX paragraphs and image, hidden shapes, inherited group and hidden slide")


def reorder(source,target,middle):
    from xml.etree import ElementTree as ET
    from zipfile import ZipFile
    with ZipFile(source) as src, ZipFile(target,"x") as dest:
        for part in src.infolist():
            data=src.read(part)
            if part.filename=="ppt/presentation.xml":
                deck=ET.fromstring(data)
                order=deck.find("{http://schemas.openxmlformats.org/presentationml/2006/main}sldIdLst")
                assert len(order)==3
                order[:]=list(reversed(order))
                data=ET.tostring(deck,encoding="utf-8")
            elif part.filename=="ppt/slides/slide2.xml":
                if middle=="missing":
                    continue
                if middle=="damaged":
                    data=b"<"
            dest.writestr(part,data)


if __name__=="__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--make", nargs=2, type=Path, metavar=("PPTX", "POSTER"))
    modes.add_argument("--verify", type=Path)
    modes.add_argument("--reorder", nargs=2,type=Path,metavar=("SOURCE","OUTPUT"))
    parser.add_argument("--reordered",action="store_true")
    parser.add_argument("--middle",choices=("hidden","missing","damaged"),default="hidden")
    args = parser.parse_args()
    if args.make:
        make(*args.make)
    elif args.reorder:
        reorder(*args.reorder,args.middle)
    else:
        verify(args.verify,args.reordered,args.middle)
