"""MIT-licensed synthetic technical manuscript. No observations are represented as real."""
from pathlib import Path
from docx import Document
from docx.shared import Pt,Cm
from docx.oxml import OxmlElement
from docx.oxml.ns import qn

OUT=Path(__file__).resolve().parent
paragraphs=[
"为比较采样间隔对温度峰值估计的影响，我们构造了一组包含短时升温事件的合成温度序列。在研究的过程中，我们只改变采样间隔，保留相同的起始时刻、观测窗口和峰值计算方法，以便能够区分采样造成的差异与序列本身的变化。",
"合成序列以 1 s 为间隔，共持续 600 s。基线温度为 22.0 ℃。在第 303 s，温度升至 26.0 ℃，随后回落；整个升温事件持续 8 s。在相同的条件下，我们分别按 5 s 和 10 s 的间隔抽取读数。三个序列均使用观测窗口内的最大值作为峰值估计。",
"在随后的分析过程中，我们对三个序列的峰值进行了比较。1 s 序列的峰值为 26.0 ℃，5 s 序列为 24.0 ℃，10 s 序列为 22.0 ℃。值得注意的是，最后一个结果与基线相同，因为 10 s 序列的相邻采样时刻恰好落在此次事件的两侧。",
"所得到的结果依赖于升温事件相对于采样时钟的位置。改变起始时刻，即使采样间隔相同，也可能得到不同的峰值估计。这里使用的是一个预先设定的合成事件，没有引入传感器噪声，因此不能据此确定真实仪器所需要的采样频率。"]
paragraphs[1]=paragraphs[1].replace("随后回落；整个升温事件持续 8 s","第 305 s 回落至 24.0 ℃，第 308 s 恢复基线。第 300 s 仍为基线温度")
paragraphs[1]=paragraphs[1].replace("温度。。","温度。")
d=Document();d.core_properties.author="TechPaper";d.core_properties.title="采样间隔对短时温度峰值估计的影响"
d.sections[0].top_margin=Cm(2.0);d.sections[0].bottom_margin=Cm(2.0)
style=d.styles["Normal"];style.font.name="Microsoft YaHei";style.font.size=Pt(10.5)
style._element.get_or_add_rPr().rFonts.set(qn("w:eastAsia"),"微软雅黑")
style.paragraph_format.line_spacing=1.5;style.paragraph_format.space_after=Pt(5)
d.add_heading(d.core_properties.title,0)
for i,(title,text) in enumerate(zip(["问题与设定","采样方法","结果","适用范围"],paragraphs)):
    heading=d.add_heading(title,2);num=OxmlElement("w:numPr")
    for key,value in [("ilvl","0"),("numId","1")]:
        el=OxmlElement("w:"+key);el.set(qn("w:val"),value);num.append(el)
    heading._p.get_or_add_pPr().append(num)
    p=d.add_paragraph()
    if i==1:
        head,tail=text.split("三个序列",1)
        p.add_run(head);p.add_run("[1]").font.superscript=True;p.add_run("三个序列"+tail)
    else:p.add_run(text)
d.add_heading("参考文献",2);d.add_paragraph("[1] TechPaper. 公开合成采样序列说明，2026。数据与方法仅用于软件验证。")
d.sections[0].header.paragraphs[0].text="TechPaper　/　公开合成实验"
d.sections[0].footer.paragraphs[0].text="现代学术写作验证样件　·　MIT"
d.save(OUT/"before.docx")
