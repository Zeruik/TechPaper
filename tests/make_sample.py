from pathlib import Path
from docx import Document
from docx.shared import Pt
from docx.oxml import OxmlElement
from docx.oxml.ns import qn

doc = Document()
doc.core_properties.author = "Fixture Author"
doc.core_properties.last_modified_by = "Fixture Editor"
doc.add_heading("实验室通风记录", 0)
doc.add_heading("1  观测方法", 1)
para = doc.add_paragraph()
para.paragraph_format.space_after = Pt(9)
para.paragraph_format.line_spacing = 1.5
para.add_run("值得注意的是，").bold = True
para.add_run("在研究的过程中，我们每隔 15 分钟记录一次室内温度。")
doc.add_paragraph("在实际应用的过程中，两组传感器分别放置在门口和房间中央。")
para = doc.add_paragraph("读数单位为 m")
para.add_run("3").font.superscript = True
para.add_run("，仪器编号与采样日期见附表。")
doc.add_heading("2  观测记录", 1)
para = doc.add_paragraph("两组数据采用相同的采样时钟，以减少记录时间的偏差。")
doc.add_comment(para.runs, text="Fixture private comment", author="Fixture Reviewer", initials="FR")
para = doc.add_paragraph("传感器编号与通道映射见第一个实验记录。")
pr = para._p.get_or_add_pPr()
num = OxmlElement("w:numPr")
for tag, value in [("ilvl", "0"), ("numId", "1")]:
    node = OxmlElement("w:" + tag)
    node.set(qn("w:val"), value)
    num.append(node)
pr.append(num)
table = doc.add_table(rows=2, cols=2)
for row, values in zip(table.rows,[["位置","读数"],["门口","21.5 °C"]]):
    for cell, value in zip(row.cells,values):
        cell.text = value
doc.sections[0].header.paragraphs[0].text = "研究记录"
doc.sections[0].footer.paragraphs[0].text = "校对稿"
para = doc.add_paragraph("值得注意的是，我们使用同一仪器记录了室温")
para.add_run("[7]").font.superscript = True
para.add_run("，并对两次读数取平均值。")
para=doc.add_paragraph("这里演示保留修订记录。")
ins=OxmlElement("w:ins")
ins.set(qn("w:id"),"5");ins.set(qn("w:author"),"Fixture Editor")
run=OxmlElement("w:r");txt=OxmlElement("w:t");txt.text="修订中的文字";run.append(txt);ins.append(run);para._p.append(ins)
doc.add_section()
doc.add_picture(str(Path(__file__).parents[1]/"assets"/"logo.png"),width=Pt(36))
doc.add_heading("参考文献",1)
doc.add_paragraph("值得注意的是，此段用于验证参考文献区的原文保护。")
doc.save(Path(__file__).with_name("sample.docx"))
