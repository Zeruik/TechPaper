"""Local extraction, evidence and offline HTML. No network clients are used."""
import argparse
import collections
import html
import json
import os
from pathlib import Path
import posixpath
import re
import sys
import tempfile
import zipfile

from defusedxml import ElementTree as ET

W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
A = "{http://schemas.openxmlformats.org/drawingml/2006/main}"
R = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
P = "{http://schemas.openxmlformats.org/presentationml/2006/main}"
ENGINE = "TechPaper Style Baseline 0.2"
AI = re.compile(r"值得注意的是|不可忽视的是|综上所述|至关重要|深远影响|it is (?:important|worth) (?:to note|noting)|in conclusion|plays? a (?:crucial|pivotal) role",re.I)
PERSONAL = re.compile(r"我(?:手工|亲自|记录|重测|重新|测量)|I (?:measured|counted|rechecked|recorded|repeated)\b",re.I)
SENT = re.compile(r"[^。！？!?\n]+?(?:[。！？!?]+|(?<!\d)\.(?=\s|$)|(?=\n|$))|[\s\S]",re.S)
ocr_engine = None


def evidence(sentence):
    if len(sentence.strip()) < 18:
        return "未判断", "不足", "短句，线索不足"
    a, h = len(AI.findall(sentence)), bool(PERSONAL.search(sentence) and re.search(r"\d",sentence))
    if a and h:
        return "MIX","低","模板措辞与具体第一人称观测同时出现"
    if a >= 2:
        return "AI","低","至少两处预定义模板措辞"
    if h:
        return "HUMAN","低","第一人称实测描述与数字同时出现"
    return "未判断","不足","无足够的预定义线索"


def rels(z, part):
    path=posixpath.join(posixpath.dirname(part),"_rels",posixpath.basename(part)+".rels")
    if path not in z.namelist():
        return {}
    return {n.get("Id"):posixpath.normpath(posixpath.join(posixpath.dirname(part),n.get("Target",""))).lstrip("/")
            for n in ET.fromstring(z.read(path)) if n.get("TargetMode")!="External"}


def column_order(rows, width):
    """Read two sustained columns between spanning headings in portrait posters."""
    result=[]
    pending=[]
    def flush():
        left=[row for row in pending if max(x for x,y in row[0]) < width*.48]
        right=[row for row in pending if min(x for x,y in row[0]) > width*.52]
        if min(len(left),len(right)) >= 3:
            top=lambda row:min(y for x,y in row[0])
            if max(min(map(top,left)),min(map(top,right))) < min(max(map(top,left)),max(map(top,right))):
                result.extend(sorted(left,key=top)+sorted(right,key=top))
                pending.clear()
                return
        result.extend(pending)
        pending.clear()
    for row in rows:
        xs=[x for x,y in row[0]]
        if max(xs)<width*.48 or min(xs)>width*.52:
            pending.append(row)
        else:
            flush()
            result.append(row)
    flush()
    return result


def ocr(image, location, offset=(0,0), columns=False):
    global ocr_engine
    import numpy as np
    import onnxruntime
    onnxruntime.disable_telemetry_events()
    from rapidocr_onnxruntime import RapidOCR
    if ocr_engine is None:
        ocr_engine=RapidOCR(intra_op_num_threads=2,inter_op_num_threads=2,text_score=0.0)
    results,_ = ocr_engine(np.asarray(image.convert("RGB"))[:,:,::-1].copy())
    if not results:
        yield location,"","OCR 未识别文字",None
    else:
        for i,(box,text,prob) in enumerate(column_order(results,image.width) if columns else results,1):
            coords=" ".join("%d,%d"%(x+offset[0],y+offset[1]) for x,y in box)
            yield f"{location} / 行 {i} / px {coords}",text,"OCR；阅读顺序待核",float(prob)


def office(path):
    from PIL import Image
    with zipfile.ZipFile(path) as z:
        if path.suffix.lower()==".pptx":
            part="ppt/presentation.xml"
            deck=ET.fromstring(z.read(part))
            lookup=rels(z,part)
            for index,slide in enumerate(deck.findall(".//"+P+"sldId"),1):
                target=lookup[slide.get(R+"id")]
                root=ET.fromstring(z.read(target))
                for j,paragraph in enumerate(root.iter(A+"p"),1):
                    t="".join(n.text or "" for n in paragraph.iter(A+"t"))
                    if t.strip(): yield f"幻灯片 {index} / 段 {j}",t,"幻灯片文字层；形状顺序待核",None
                yield from embedded(z,root,target,f"幻灯片 {index}")
        else:
            parts=["word/document.xml"]
            parts += [name for name in z.namelist() if re.fullmatch(r"word/(?:footnotes|endnotes|header\d+|footer\d+)\.xml",name)]
            for part in parts:
                count=0
                with z.open(part) as source:
                    for _,p in ET.iterparse(source,events=("end",)):
                        if p.tag!=W+"p": continue
                        count+=1
                        def visible(node):
                            if node.tag in (W+"del",W+"moveFrom"): return ""
                            if node.tag==W+"r" and any(x.get(W+"val","true").lower() not in ("false","0","off") for x in node.findall(W+"rPr/"+W+"vanish")):return ""
                            if node.tag==W+"t":return node.text or ""
                            if node.tag==W+"tab":return "\t"
                            if node.tag in (W+"br",W+"cr"):return "\n"
                            return "".join(visible(child) for child in node)
                        t=visible(p)
                        loc=f"{part.split('/')[-1]} / 段 {count}"
                        if t.strip():yield loc,t,"DOCX 文字层；物理页码依赖排版",None
                        yield from embedded(z,p,part,loc)
                        p.clear()


def embedded(z,node,part,loc):
    from PIL import Image
    images=list(node.iter(A+"blip"))
    if not images:return
    lookup=rels(z,part)
    for i,img in enumerate(images,1):
        where=f"{loc} / 图 {i}（原图像素）"
        target=lookup.get(img.get(R+"embed"))
        try:
            if not target:raise ValueError()
            with z.open(target) as data, Image.open(data) as photo:
                yield from ocr(photo,where)
        except Exception as ex:
            yield where,"","图像跳过："+type(ex).__name__,None


def extract(path):
    from PIL import Image,ImageOps
    ext=path.suffix.lower()
    if ext in (".docx",".pptx"):
        yield from office(path)
    elif ext in (".png",".jpg",".jpeg"):
        with Image.open(path) as picture:
            yield from ocr(ImageOps.exif_transpose(picture),"图 1",columns=True)
    elif ext==".pdf":
        import pypdfium2 as pdf
        with pdf.PdfDocument(path) as document:
            for i in range(len(document)):
                page=document[i]
                try:
                    text=page.get_textpage()
                    try:
                        if text.get_text_bounded().strip():
                            # PDFium indices identify each physical occurrence, including repeated lines.
                            data=[];bounds=[];line=0
                            for j in range(text.count_chars()+1):
                                ch=chr(pdf.raw.FPDFText_GetUnicode(text,j)) if j<text.count_chars() else "\n"
                                if ch in "\r\n":
                                    if data:
                                        line+=1
                                        box="" if not bounds else " / pt %.1f,%.1f,%.1f,%.1f"%(min(b[0] for b in bounds),min(b[1] for b in bounds),max(b[2] for b in bounds),max(b[3] for b in bounds))
                                        yield f"第 {i+1} 页 / 行 {line}{box}","".join(data),"PDF 文字层；坐标原点左下，读序待核",None
                                        data=[];bounds=[]
                                else:
                                    data.append(ch)
                                    try:bounds.append(text.get_charbox(j))
                                    except Exception:pass
                            images=list(page.get_objects(filter=[pdf.raw.FPDF_PAGEOBJ_IMAGE],max_depth=0))
                            if images:
                                bitmap=page.render(scale=2)
                                try:
                                    view=bitmap.to_pil()
                                    conv=bitmap.get_posconv(page)
                                    seen=set()
                                    for idx,obj in enumerate(images,1):
                                        loc=f"第 {i+1} 页 / 图 {idx} / 144 dpi"
                                        x0,y0,x1,y1=obj.get_bounds()
                                        corners=[conv.to_bitmap(x,y) for x in (x0,x1) for y in (y0,y1)]
                                        xs,ys=zip(*corners)
                                        box=(max(0,min(xs)),max(0,min(ys)),min(view.width,max(xs)),min(view.height,max(ys)))
                                        if box in seen or box[2]<=box[0] or box[3]<=box[1]:continue
                                        seen.add(box)
                                        for where,value,note,conf in ocr(view.crop(box),loc,box[:2],columns=True):
                                            # Suppress an OCR overlay only when the same text is selectable at this location.
                                            pts=re.findall(r"(-?\d+),(-?\d+)",where.split(" / px ")[-1])
                                            if pts and value:
                                                pagepts=[conv.to_page(int(x),int(y)) for x,y in pts]
                                                px,py=zip(*pagepts)
                                                selectable=text.get_text_bounded(min(px)-2,min(py)-2,max(px)+2,max(py)+2)
                                                if "".join(value.split()) in "".join(selectable.split()):continue
                                            yield where,value,note,conf
                                finally:bitmap.close()
                        else:
                            bitmap=page.render(scale=2)
                            try:yield from ocr(bitmap.to_pil(),f"第 {i+1} 页 / 144 dpi")
                            finally:bitmap.close()
                    finally:text.close()
                except Exception as ex:
                    yield f"第 {i+1} 页","","提取失败："+type(ex).__name__,None
                finally:page.close()
    else:raise ValueError("不支持的文件类型")


class Corpus:
    """Exact 16-character spans, with a source and offset for every reported match."""
    width=16
    def __init__(self,path):
        self.name=path.name if path else None
        self.text=path.read_text(encoding="utf-8-sig") if path else ""
        self.index={}
        for i in range(len(self.text)-self.width+1):
            key=self.text[i:i+self.width]
            self.index.setdefault(key,i)
    def match(self,text):
        if not self.name:return "未选择语料"
        for i in range(len(text)-self.width+1):
            key=text[i:i+self.width]
            if key in self.index:
                j=self.index[key];length=self.width
                while i+length<len(text) and j+length<len(self.text) and text[i+length]==self.text[j+length]:
                    length+=1
                return f"{self.name} · 字符 {j}–{j+length} · {text[i:i+length]}"
        return "未命中连续 16 字符"


def report(path,dest,corpus_path=None):
    if dest.exists():raise FileExistsError("报告已存在，请选用新文件名")
    corpus=Corpus(corpus_path)
    counts=collections.Counter();esc=html.escape
    fd,name=tempfile.mkstemp(dir=dest.parent,prefix=".techpaper-",suffix=".tmp")
    try:
        with os.fdopen(fd,"w",encoding="utf-8") as out:
            out.write("""<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><meta http-equiv="Content-Security-Policy" content="default-src 'none';style-src 'unsafe-inline'"><title>TechPaper · 原文审阅报告</title><style>body{max-width:1100px;margin:auto;padding:2rem;font:16px/1.65 system-ui;color:#182d2a;background:#f9faf6}h1{font: bold 36px Georgia,serif}article{border-bottom:1px solid #b7c9bd;padding:1em 0}small{color:#415047}mark{white-space:pre-wrap;color:#122622;background:#e3e6e1}.AI{background:#ffd7c6}.MIX{background:#fae5a3}.HUMAN{background:#c6e8d1}article{display:grid;grid-template-columns:minmax(0,2fr) minmax(0,1fr);gap:.4em 1.25em}article small{grid-column:1/-1}aside{font-size:.9rem;border-left:2px solid #b7c9bd;padding-left:1em;overflow-wrap:anywhere}mark{overflow-wrap:anywhere}@media(max-width:640px){article{display:block}aside{margin-top:.6em}}section{padding:1em;background:white}</style>""")
            out.write(f"<h1>TechPaper</h1><h2>{esc(path.name)}</h2><p>{ENGINE} · 离线规则 · <a href='#coverage'>查看覆盖统计</a></p><p>AI / MIX / HUMAN 表示规则匹配方向，低置信提示未经概率校准。未判断表示弃答。颜色与标签对应。句级结果不能推算作者身份或 AI 字数比例。</p>")
            out.write("<p>来源比对仅检索所选 UTF-8 TXT 的连续相同字符。OCR 坐标为图像左上角像素，低于 0.75 时跳过分类。段内偏移从 0 开始，末端不含。</p>")
            for n,(loc,text,kind,prob) in enumerate(extract(path),1):
                if n%20==1:print(f"正在分析位置 {n}",flush=True)
                if not text.strip():
                    counts["skips"]+=1
                    out.write(f"<article><b>未分析</b> · {esc(loc)} · {esc(kind)}</article>")
                    continue
                for m in SENT.finditer(text):
                    sentence=m.group()
                    if not sentence.strip():continue
                    label,confidence,why=evidence(sentence) if prob is None or prob>=.75 else ("未分析","不足","OCR 低置信")
                    counts[label]+=1
                    counts["total"]+=1
                    score="" if prob is None else f" · OCR {prob:.2f}"
                    out.write(f"<article id='p{n}s{m.start()}'><small>{esc(loc)} · 字符 {m.start()}–{m.end()} · {esc(kind)}{score}</small><div><b>写作信号　{label} · {confidence}</b>　{esc(why)}<br><mark class='{label}'>{esc(sentence)}</mark></div><aside><b>本地语料</b><br>{esc(corpus.match(sentence))}</aside></article>")
            total=counts["total"];assigned=sum(counts[k] for k in ("AI","MIX","HUMAN"))
            summary=f"共 {total} 个非空片段，分类 {assigned}（{assigned/max(total,1):.1%}），弃答 {counts['未判断']}，低置信未分析 {counts['未分析']}，空白/跳过/失败位置 {counts['skips']}。"
            details=" · ".join(f"{k} {counts[k]}" for k in ("AI","MIX","HUMAN"))
            out.write(f"<section id='coverage'><h2>覆盖统计</h2><p>{summary}</p>{details}<p>分母为成功提取的非空文本片段，跳过的图像和页面单列。无文字时不执行分类。</p></section></html>")
        # Windows rename refuses to replace an existing destination.
        if os.name=="nt":os.rename(name,dest)
        else:os.link(name,dest)
        print(summary,flush=True)
    finally:Path(name).unlink(missing_ok=True)


if __name__=="__main__":
    def offline(event, args):
        if event in {"socket.connect","socket.getaddrinfo","socket.sendto"}:
            raise PermissionError("离线分析进程已禁用网络")
    sys.addaudithook(offline)
    sys.stdout.reconfigure(encoding="utf-8")
    args=argparse.ArgumentParser()
    args.add_argument("source",type=Path);args.add_argument("output",type=Path);args.add_argument("--corpus",type=Path)
    opts=args.parse_args()
    try:report(opts.source,opts.output,opts.corpus)
    except Exception as e:
        print("未完成："+str(e),flush=True);sys.exit(1)
