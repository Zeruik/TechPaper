"""Generate equivalent synthetic visibility fixtures and verify installed-worker HTML."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import tempfile
from zipfile import ZipFile


def make(folder):
    import pymupdf as pdf
    folder.mkdir(parents=True,exist_ok=True)
    lines=[(170,54,"样件 A | Sample A"),(28,132,"室温 22.5 °C"),(28,200,"零点记录"),
           (320,132,"Tolerance 0.25 mm"),(320,200,"零点记录")]
    (folder/"expected.json").write_text(json.dumps(lines,ensure_ascii=False),encoding="utf8")
    def draw(page, invisible=False, conflict=False):
        for i in (0,1,3,2,4):
            x,y,text=lines[i]
            page.insert_text((x,y),text.replace("0.25","0.35") if conflict else text,
                             fontsize=19,fontname="lab",fontfile="C:/Windows/Fonts/msyh.ttc",
                             render_mode=3 if invisible else 0)
    for name in ("ordinary","flat","overlay","hidden","conflict"):
        with pdf.open() as doc:
            page=doc.new_page(width=550,height=250)
            if name=="ordinary":
                draw(page)
                page.get_pixmap(matrix=pdf.Matrix(2,2)).save(folder/"poster.png")
            elif name!="hidden":
                page.insert_image(page.rect,filename=str(folder/"poster.png"))
            if name in ("hidden","overlay","conflict"):
                draw(page,True,name=="conflict")
            doc.subset_fonts()
            doc.save(folder/(name+".pdf"))
    from docx import Document
    doc=Document()
    for i in range(12000):
        doc.add_paragraph(f"Record {i:05d} is a synthetic observation.")
    doc.save(folder/"long.docx")


def verify(folder, worker):
    from lxml import html
    expected=json.loads((folder/"expected.json").read_bytes())
    with tempfile.TemporaryDirectory() as tmp:
        for name in ("ordinary.pdf","poster.png","flat.pdf","overlay.pdf","hidden.pdf","conflict.pdf","long.docx"):
            output=Path(tmp)/(name+".html")
            result=subprocess.run([str(worker),str(folder/name),str(output)],capture_output=True)
            if result.returncode:raise RuntimeError(name+" worker failed")
            tree=html.fromstring(output.read_text(encoding="utf8"))
            articles=tree.xpath("//article[.//mark]")
            ids=[a.get("id") for a in articles]
            assert None not in ids and len(ids)==len(set(ids)),name
            summary=tree.xpath("//section[@id='coverage']/p")[0].text_content()
            labels=[a.find(".//mark").get("class") for a in articles]
            assigned=sum(label in ("AI","MIX","HUMAN") for label in labels)
            rate=f"{assigned/len(labels):.1%}" if labels else "未提取到正文，覆盖率不适用"
            skips=int(name=="hidden.pdf")
            assert summary==(
                f"共 {len(labels)} 个非空片段，分类 {assigned}（{rate}），弃答 {labels.count('未判断')}，"
                f"低置信未分析 {labels.count('未分析')}，空白/跳过/失败位置 {skips}。"
            ),(name,summary)
            if name=="long.docx":
                assert len(articles)==12000
                for i,a in enumerate(articles):
                    assert a.find(".//mark").text==f"Record {i:05d} is a synthetic observation."
                print("PASS long DOCX · 12000/12000",flush=True)
                continue
            targets=[] if name=="hidden.pdf" else expected
            assert len(targets)==len(articles),(name,len(articles))
            for a,(x,y,text) in zip(articles,targets):
                mark=a.find(".//mark").text
                assert "".join(mark.split())=="".join(text.split()),(name,mark)
                location=a.find("small").text
                assert f"字符 0–{len(mark)} " in location
                if " / pt " in location:
                    box=list(map(float,location.split(" / pt ")[1].split(" ·")[0].split(",")))
                    left,top=box[0],250-box[3]
                else:
                    points=list(map(float,re.findall(r"-?\d+",location.split(" / px ")[1].split(" ·")[0])))
                    left,top=min(points[::2])/2,min(points[1::2])/2
                assert abs(left-x)<8 and y-26<top<y,(name,left,top)
            if name in ("hidden.pdf","conflict.pdf","overlay.pdf"):
                assert "不可见文字层" in tree.text_content()
            print("PASS",name,len(articles),"lines; reading order, values, anchors, highlight ranges",flush=True)


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("folder",type=Path)
    parser.add_argument("--installed-worker",type=Path)
    args=parser.parse_args()
    if args.installed_worker:verify(args.folder,args.installed_worker)
    else:make(args.folder)
