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
M = "{http://schemas.openxmlformats.org/officeDocument/2006/math}"
ENGINE = "TechPaper Style Baseline 0.2.1"
VISIBLE_RENDER_NOTICE = "检测到不可见文字层；本页按可见渲染识别，隐藏数值不参与分类"
OUTLINE_HEADING = "DOCX 大纲标题"
BIBLIOGRAPHY = "DOCX 参考文献样式"
BIBLIOGRAPHY_CONTROL = "DOCX 参考文献内容控件"
DOCX_BODY = "DOCX 文字层；物理页码依赖排版"
EXCLUDED = {
    OUTLINE_HEADING: ("标题", "未分析", "按 Word 大纲级别排除，保留原文与语料比对"),
    BIBLIOGRAPHY: ("参考文献", "未分析", "按 Word 参考文献段落样式排除，保留原文与语料比对"),
    BIBLIOGRAPHY_CONTROL: ("参考文献", "未分析", "按 Word 参考文献内容控件排除，保留原文与语料比对"),
}
AI = re.compile(r"值得注意的是|不可忽视的是|综上所述|至关重要|深远影响|it is (?:important|worth) (?:to note|noting)|in conclusion|plays? a (?:crucial|pivotal) role",re.I)
PERSONAL = re.compile(r"我(?:手工|亲自|记录|重测|重新|测量)|\bI (?:measured|counted|rechecked|recorded|repeated)\b",re.I)
SENT = re.compile(r"[^。！？!?\n]+?(?:[。！？!?]+|\.(?=\s|$)|(?=\n|$))|[\s\S]",re.S)
ocr_engine = None


def evidence(sentence):
    if len(sentence.strip()) < 18:
        return "未判断", "不足", "短句，线索不足"
    a, h = len(AI.findall(sentence)), bool(PERSONAL.search(sentence) and re.search(r"\d",sentence))
    if a and h:
        return "MIX","低","模板措辞、第一人称观测表述和数字同时出现；生成文本也可含这些线索"
    if a >= 2:
        return "AI","低","至少两处预定义模板措辞"
    if h:
        return "HUMAN","低","第一人称观测表述和数字同时出现；生成文本也可含这些线索"
    return "未判断","不足","无足够的预定义线索"


def rels(z, part):
    path=posixpath.join(posixpath.dirname(part),"_rels",posixpath.basename(part)+".rels")
    if path not in z.namelist():
        return {}
    return {n.get("Id"):posixpath.normpath(posixpath.join(posixpath.dirname(part),n.get("Target",""))).lstrip("/")
            for n in ET.fromstring(z.read(path)) if n.get("TargetMode")!="External"}


def poster_gutter(rows, width):
    """Estimate a gutter from pairs of substantial text lines on the same baseline."""
    votes = []
    for box, _, _ in rows:
        xs, ys = zip(*box)
        if max(xs)-min(xs) >= .10*width:
            votes.append((min(xs), max(xs), min(ys), max(ys)))
    cuts = [width * percent / 100 for percent in range(30,71,2)]
    scores = [0] * len(cuts)
    margin = width * .02
    votes.sort(key=lambda bounds: bounds[2])
    for i, first in enumerate(votes):
        for j in range(i+1,len(votes)):
            other = votes[j]
            if other[2] >= first[3]:
                break
            overlap = min(first[3],other[3]) - other[2]
            if overlap < .6*min(first[3]-first[2], other[3]-other[2]):
                continue
            left, right = sorted((first, other))
            for pos, cut in enumerate(cuts):
                if left[1]+margin < cut < right[0]-margin:
                    scores[pos] += 1
    best = max(scores)
    if best < 2:
        return width / 2
    return min((cut for cut,score in zip(cuts,scores) if score==best), key=lambda cut:abs(width/2-cut))


def column_order(rows, width, gutter=None):
    """Read two sustained columns between spanning headings in portrait posters."""
    result=[]
    pending=[]
    center = width/2 if gutter is None else gutter
    left_edge, right_edge = center-width*.02, center+width*.02
    def flush():
        left=[row for row in pending if max(x for x,y in row[0]) < left_edge]
        right=[row for row in pending if min(x for x,y in row[0]) > right_edge]
        if min(len(left),len(right)) >= 2:
            top=lambda row:min(y for x,y in row[0])
            if max(min(map(top,left)),min(map(top,right))) < min(max(map(top,left)),max(map(top,right))):
                result.extend(sorted(left,key=top)+sorted(right,key=top))
                pending.clear()
                return
        result.extend(pending)
        pending.clear()
    for row in rows:
        xs=[x for x,y in row[0]]
        if max(xs)<left_edge or min(xs)>right_edge:
            pending.append(row)
        else:
            flush()
            result.append(row)
    flush()
    return result


def ocr(image, location, offset=(0,0), columns=False):
    global ocr_engine
    from PIL import Image
    import numpy as np
    import onnxruntime
    onnxruntime.disable_telemetry_events()
    from rapidocr_onnxruntime import RapidOCR
    if ocr_engine is None:
        ocr_engine=RapidOCR(intra_op_num_threads=2,inter_op_num_threads=2,text_score=0.0)
    if "A" in image.getbands() or "transparency" in image.info:
        rgba = image.convert("RGBA")
        visible = Image.new("RGB", image.size, "white")
        visible.paste(rgba, mask=rgba.getchannel("A"))
        location += "（透明区域按白底合成）"
    else:
        visible = image.convert("RGB")
    results,_ = ocr_engine(np.asarray(visible)[:,:,::-1].copy())
    if not results:
        yield location,"","OCR 未识别文字",None
    else:
        for i,(box,text,prob) in enumerate(column_order(results,image.width,poster_gutter(results,image.width)) if columns else results,1):
            text=re.sub(r"(?<=[A-Za-z0-9])\s*丨\s*(?=[A-Za-z])"," | ",text)
            coords=" ".join("%d,%d"%(x+offset[0],y+offset[1]) for x,y in box)
            yield f"{location} / 行 {i} / px {coords}",text,"OCR；阅读顺序待核",float(prob)


def visible_slide_rows(z, root, part, index):
    location = f"幻灯片 {index}"
    if root.get("show", "true").strip() in ("0", "false"):
        yield location, "", "放映时隐藏的幻灯片已跳过", None
        return
    # Keep source ordinals even when earlier shapes are hidden.
    positions = {node:i for i, node in enumerate(root.iter(A+"p"), 1)}
    image_positions = {node:i for i, node in enumerate(root.iter(A+"blip"), 1)}
    stack = [root]
    while stack:
        node = stack.pop()
        props = node.find("*/"+P+"cNvPr")
        if props is not None and props.get("hidden", "false").strip() in ("1", "true"):
            yield f"{location} / 对象 {props.get('id', '?')}", "", "显式隐藏的幻灯片对象已跳过", None
            node.clear()
            continue
        stack.extend(reversed(list(node)))
    for paragraph in root.iter(A+"p"):
        text = "".join(n.text or "" for n in paragraph.iter(A+"t"))
        if text.strip():
            yield f"{location} / 段 {positions[paragraph]}", text, "幻灯片文字层；形状顺序待核", None
    yield from embedded(z, root, part, location, image_positions)


def office(path):
    with zipfile.ZipFile(path) as z:
        if path.suffix.lower()==".pptx":
            part="ppt/presentation.xml"
            deck=ET.fromstring(z.read(part))
            lookup=rels(z,part)
            for index,slide in enumerate(deck.findall(".//"+P+"sldId"),1):
                target=lookup[slide.get(R+"id")]
                root=ET.fromstring(z.read(target))
                yield from visible_slide_rows(z,root,target,index)
        else:
            style_root = ET.fromstring(z.read("word/styles.xml")) if "word/styles.xml" in z.namelist() else None
            styles = {style.get(W+"styleId"): style for style in (() if style_root is None else style_root.findall(W+"style")) if style.get(W+"styleId")}
            defaults = {style.get(W+"type"): key for key, style in styles.items() if style.get(W+"default") in ("1","true","on")}
            default_style = defaults.get("paragraph")
            hidden = run_visibility(style_root, styles, defaults)
            parts=["word/document.xml"]
            parts += [name for name in z.namelist() if re.fullmatch(r"word/(?:footnotes|endnotes|header\d+|footer\d+)\.xml",name)]
            for part in parts:
                count=0
                with z.open(part) as source:
                    stack = []
                    revision_depth = 0
                    for event, node in ET.iterparse(source, events=("start", "end")):
                        revision = node.tag in (W+"del", W+"moveFrom")
                        if event == "start":
                            stack.append(node)
                            revision_depth += int(revision)
                            continue
                        stack.pop()
                        if node.tag == W+"p":
                            count += 1
                            paragraph = None
                            hidden_ancestor = False
                            for ancestor in stack:
                                if ancestor.tag == W+"p":
                                    paragraph = ancestor
                                elif ancestor.tag == W+"r" and hidden(ancestor, paragraph):
                                    hidden_ancestor = True
                                    break
                            if not revision_depth and not hidden_ancestor:
                                loc = f"{part.split('/')[-1]} / 段 {count}"
                                kind = paragraph_kind(node, styles, default_style)
                                if kind==DOCX_BODY and any(a.tag==W+"sdt" and a.find(W+"sdtPr/"+W+"bibliography") is not None for a in stack):
                                    kind=BIBLIOGRAPHY_CONTROL
                                yield from docx_review_rows(z, node, part, loc, kind)
                            node.clear()
                        elif revision or (node.tag == W+"r" and hidden(node, next((p for p in reversed(stack) if p.tag == W+"p"), None))):
                            node.clear()
                        revision_depth -= int(revision)


def run_visibility(root, styles, defaults):
    def enabled(flag):
        return flag is not None and flag.get(W+"val", "true") in ("1", "true", "on")

    default_hidden = root is not None and enabled(root.find(W+"docDefaults/"+W+"rPrDefault/"+W+"rPr/"+W+"vanish"))
    toggles = {}

    def toggle(ref, kind):
        key = ref.get(W+"val") if ref is not None else None
        if key not in styles:
            key = defaults.get(kind)
        if key not in toggles:
            current = key
            state = False
            seen = set()
            while current in styles:
                if current in seen:
                    raise ValueError("Word 样式继承存在循环，无法确定正文可见性。")
                seen.add(current)
                style = styles[current]
                state ^= enabled(style.find(W+"rPr/"+W+"vanish"))
                parent = style.find(W+"basedOn")
                current = parent.get(W+"val") if parent is not None else None
            toggles[key] = state
        return toggles[key]

    def hidden(run, paragraph):
        direct = run.find(W+"rPr/"+W+"vanish")
        if direct is not None:
            return enabled(direct)
        para_style = paragraph.find(W+"pPr/"+W+"pStyle") if paragraph is not None else None
        return default_hidden ^ toggle(para_style, "paragraph") ^ toggle(run.find(W+"rPr/"+W+"rStyle"), "character")
    return hidden


def paragraph_kind(node, styles, default_style):
    style = node.find(W+"pPr/"+W+"pStyle")
    key = style.get(W+"val") if style is not None else default_style
    seen = set()
    outline = None
    bibliography = False
    while node is not None:
        level = node.find(W+"pPr/"+W+"outlineLvl")
        if outline is None and level is not None:
            outline = level.get(W+"val") in ("0","1","2","3","4","5","6","7","8")
        if key not in styles or key in seen:
            break
        seen.add(key)
        node = styles[key]
        name = node.find(W+"name")
        if name is not None and name.get(W+"val", "").strip().casefold() in ("bibliography", "参考文献"):
            bibliography = True
        parent = node.find(W+"basedOn")
        key = parent.get(W+"val") if parent is not None else None
    if outline:
        return OUTLINE_HEADING
    return BIBLIOGRAPHY if bibliography else DOCX_BODY


def docx_review_rows(z, node, part, loc, reason):
    skipped = "原生 Word 公式未纳入文字分析"
    chunks = [[]]
    objects = []
    for token in docx_text(node):
        if not isinstance(token, str):
            objects.append(token)
            chunks.append([])
        else:
            chunks[-1].append(token)
    formulas = images = 0
    lookup = None
    texts = ["".join(tokens) for tokens in chunks]
    split_text = any(obj.tag == M+"oMath" for obj in objects) or sum(bool(t.strip()) for t in texts)>1
    for i, chunk in enumerate(texts):
        if i:
            obj = objects[i-1]
            if obj.tag == M+"oMath":
                formulas += 1
                yield f"{loc} / 公式 {formulas}", "", skipped, None
            else:
                images += 1
                if lookup is None:
                    lookup = rels(z,part)
                yield from embedded_image(z, obj, lookup, loc, images, columns=True)
        if chunk.strip():
            segment = f"{loc} / 文字片段 {i+1}" if split_text else loc
            yield segment, chunk, reason, None


def docx_text(node):
    if node.tag in (M+"oMath", A+"blip"):
        yield node
    elif node.tag == W+"t":
        yield node.text or ""
    elif node.tag == W+"tab":
        yield "\t"
    elif node.tag in (W+"br", W+"cr"):
        yield "\n"
    else:
        for child in node:
            yield from docx_text(child)


def embedded(z,node,part,loc,image_positions=None):
    images=list(node.iter(A+"blip"))
    if not images:return
    lookup=rels(z,part)
    for i,img in enumerate(images,1):
        if image_positions is not None:
            i = image_positions[img]
        yield from embedded_image(z, img, lookup, loc, i)


def embedded_image(z, img, lookup, loc, index, columns=False):
    from PIL import Image
    where=f"{loc} / 图 {index}（原图像素）"
    target=lookup.get(img.get(R+"embed"))
    try:
        if not target:raise ValueError()
        with z.open(target) as data, Image.open(data) as photo:
            yield from ocr(photo,where,columns=columns)
    except Exception as ex:
        yield where,"","图像跳过："+type(ex).__name__,None


def native_lines(text, pdf, page_width):
    """Separate adjacent columns before sorting by column. PDF coordinates start at bottom left."""
    lines, chars, boxes = [], [], []
    def flush():
        if boxes and "".join(chars).strip():
            x0=min(b[0] for b in boxes)
            y0=min(b[1] for b in boxes)
            x1=max(b[2] for b in boxes)
            y1=max(b[3] for b in boxes)
            lines.append(([(x0,-y1),(x1,-y0)],"".join(chars).strip().replace("\xa0"," "),(x0,y0,x1,y1)))
        chars.clear()
        boxes.clear()
    for j in range(text.count_chars()):
        ch=chr(pdf.raw.FPDFText_GetUnicode(text,j))
        if ch in "\r\n":
            flush()
            continue
        if ch.isspace():
            chars.append(" ")
            continue
        b=text.get_charbox(j)
        if boxes and b[0]-boxes[-1][2]>max(18,2*(b[3]-b[1])):
            flush()
        chars.append(ch)
        boxes.append(b)
    flush()
    return column_order(lines,page_width)


def hidden_text_object(obj, raw):
    import ctypes
    mode=raw.FPDFTextObj_GetTextRenderMode(obj)
    if mode in (raw.FPDF_TEXTRENDERMODE_INVISIBLE,raw.FPDF_TEXTRENDERMODE_CLIP):
        return True
    def transparent(get_color):
        channels=[ctypes.c_uint() for _ in range(4)]
        return get_color(obj,*(ctypes.byref(c) for c in channels)) and channels[-1].value==0
    fill=mode in (0,2,4,6)
    stroke=mode in (1,2,5,6)
    return (fill or stroke) and (not fill or transparent(raw.FPDFPageObj_GetFillColor)) and (not stroke or transparent(raw.FPDFPageObj_GetStrokeColor))


def image_page_corners(image):
    # PDFium returns image corners in the enclosing form's coordinate system.
    corners = image.get_quad_points()
    form = image.container
    while form is not None:
        matrix = form.get_matrix()
        corners = [matrix.on_point(x, y) for x, y in corners]
        form = form.container
    return corners


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
                        hidden=any(hidden_text_object(obj,pdf.raw) for obj in page.get_objects(filter=[pdf.raw.FPDF_PAGEOBJ_TEXT]))
                        if hidden:
                            # Render once so overlapping invisible OCR and visible content cannot both enter the report.
                            yield f"第 {i+1} 页","",VISIBLE_RENDER_NOTICE,None
                        if not hidden and text.get_text_bounded().strip():
                            for line,(_,value,bounds) in enumerate(native_lines(text,pdf,page.get_width()),1):
                                box=" / pt %.1f,%.1f,%.1f,%.1f"%bounds
                                yield f"第 {i+1} 页 / 行 {line}{box}",value,"PDF 文字层；坐标原点左下，读序待核",None
                            images=list(page.get_objects(filter=[pdf.raw.FPDF_PAGEOBJ_IMAGE]))
                            if images:
                                bitmap=page.render(scale=2)
                                try:
                                    view=bitmap.to_pil()
                                    conv=bitmap.get_posconv(page)
                                    seen=set()
                                    for idx,obj in enumerate(images,1):
                                        loc=f"第 {i+1} 页 / 图 {idx} / 144 dpi"
                                        corners=[conv.to_bitmap(x,y) for x,y in image_page_corners(obj)]
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
                            try:yield from ocr(bitmap.to_pil(),f"第 {i+1} 页 / 144 dpi",columns=True)
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
        self.text=path.read_bytes().decode("utf-8-sig") if path else ""
        self.index={}
        self.repeated=set()
        for i in range(len(self.text)-self.width+1):
            key=self.text[i:i+self.width]
            if key in self.index:
                self.repeated.add(key)
            else:
                self.index[key] = i

    def spans(self,text):
        i = 0
        while i <= len(text)-self.width:
            key=text[i:i+self.width]
            if key not in self.index:
                i += 1
                continue
            j = self.index[key]
            length = self.width
            while i+length<len(text) and j+length<len(self.text) and text[i+length]==self.text[j+length]:
                length += 1
            if key in self.repeated and i+length<len(text) and text[i:i+length+1] in self.text:
                # Repeated seed. A matching prefix implies every shorter prefix matches.
                length += 1
                upper = min(len(text)-i, len(self.text))
                while length < upper:
                    mid = (length+upper+1)//2
                    if text[i:i+mid] in self.text:
                        length = mid
                    else:
                        upper = mid-1
                j = self.text.index(text[i:i+length])
            yield i, j, length
            i += length

    def match_lines(self,text,offset):
        if not self.name:
            yield "未选择语料"
            return
        found = False
        for i, j, length in self.spans(text):
            found = True
            yield f"{self.name} · 语料字符 {j}–{j+length} · 段内字符 {offset+i}–{offset+i+length} · {text[i:i+length]}"
        if not found:
            yield "未命中连续 16 字符"


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
                if kind==VISIBLE_RENDER_NOTICE:
                    out.write(f"<article><b>提取说明</b> · {esc(loc)} · {esc(kind)}</article>")
                    continue
                if not text.strip():
                    counts["skips"]+=1
                    out.write(f"<article><b>未分析</b> · {esc(loc)} · {esc(kind)}</article>")
                    continue
                for m in SENT.finditer(text):
                    sentence=m.group()
                    if not sentence.strip():continue
                    if kind in EXCLUDED:
                        label,confidence,why=EXCLUDED[kind]
                    else:
                        label,confidence,why=evidence(sentence) if prob is None or prob>=.75 else ("未分析","不足","OCR 低置信")
                    counts[label]+=1
                    if kind not in EXCLUDED:
                        counts["total"]+=1
                    score="" if prob is None else f" · OCR {prob:.2f}"
                    if prob is not None and prob<.75 and f"{prob:.2f}"=="0.75":
                        score=" · OCR < 0.75"
                    sources = "<br>".join(esc(line) for line in corpus.match_lines(sentence,m.start()))
                    out.write(f"<article id='p{n}s{m.start()}'><small>{esc(loc)} · 字符 {m.start()}–{m.end()} · {esc(kind)}{esc(score)}</small><div><b>写作信号　{label} · {confidence}</b>　{esc(why)}<br><mark class='{label}'>{esc(sentence)}</mark></div><aside><b>本地语料</b><br>{sources}</aside></article>")
            total=counts["total"];assigned=sum(counts[k] for k in ("AI","MIX","HUMAN"))
            coverage=f"{assigned/total:.1%}" if total else "未提取到正文，覆盖率不适用"
            if (counts["标题"] or counts["参考文献"]) and not total:
                coverage="无可分类正文，覆盖率不适用"
            summary=f"共 {total} 个非空片段，分类 {assigned}（{coverage}），弃答 {counts['未判断']}，低置信未分析 {counts['未分析']}，空白/跳过/失败位置 {counts['skips']}。"
            if counts["标题"]:
                summary+=f"另列大纲标题片段 {counts['标题']}，不计入正文分母。"
            if counts["参考文献"]:
                summary+=f"另列参考文献片段 {counts['参考文献']}，不计入正文分母。"
            details=" · ".join(f"{k} {counts[k]}" for k in ("AI","MIX","HUMAN"))
            out.write(f"<section id='coverage'><h2>覆盖统计</h2><p>{summary}</p>{details}<p>分母为成功提取的非空正文片段。大纲标题、参考文献样式段落及跳过的公式、图像和页面单列。无文字时不执行分类。</p></section></html>")
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
