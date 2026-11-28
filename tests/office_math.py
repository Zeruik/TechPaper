"""Synthetic Office Math regression, with no student data."""
import argparse
import zipfile
from pathlib import Path

W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
M = "http://schemas.openxmlformats.org/officeDocument/2006/math"


def make(path):
    def run(value, ns="w"):
        return f"<{ns}:r><{ns}:t>{value}</{ns}:t></{ns}:r>"

    def math(value):
        return f"<m:oMath>{run(value, 'm')}</m:oMath>"

    fraction = "<m:oMathPara><m:oMath><m:f><m:num><m:sSub><m:e>"
    fraction += run("NUM11", "m") + "</m:e><m:sub>" + run("SUB3", "m")
    fraction += "</m:sub></m:sSub></m:num><m:den>" + run("DEN5", "m")
    fraction += "</m:den></m:f></m:oMath>" + math("NEXT8") + "</m:oMathPara>"
    paragraphs = [
        run("We repeated the test in order to estimate drift."),
        run("Before") + math("INLINE7") + run("after."),
        fraction,
        run("Revision.") + '<w:del w:id="1" w:author="Fixture">' + math("DELETED9")
        + '</w:del><w:ins w:id="2" w:author="Fixture">' + math("INSERTED5") + "</w:ins>",
    ]
    doc = f'<w:document xmlns:w="{W}" xmlns:m="{M}"><w:body>'
    doc += "".join(f"<w:p>{p}</w:p>" for p in paragraphs) + "</w:body></w:document>"
    with zipfile.ZipFile(path, "w") as package:
        package.writestr("[Content_Types].xml", '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        package.writestr("_rels/.rels", '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        package.writestr("word/document.xml", doc)


def verify(path):
    from html.parser import HTMLParser

    class Marks(HTMLParser):
        inside = False

        def handle_starttag(self, tag, attrs):
            if tag == "mark":
                self.inside = True
                marks.append("")

        def handle_endtag(self, tag):
            if tag == "mark":
                self.inside = False

        def handle_data(self, data):
            if self.inside:
                marks[-1] += data

    marks = []
    html = path.read_text(encoding="utf-8")
    Marks().feed(html)
    assert marks == ["We repeated the test in order to estimate drift.", "Before", "after.", "Revision."], marks
    notice = "原生 Word 公式未纳入文字分析"
    assert html.count(notice) == 4
    assert "document.xml / 段 3 / 公式 2" in html
    for n in (2, 3, 4):
        assert f"document.xml / 段 {n} / 公式 1" in html
    assert "共 4 个非空片段" in html
    assert all(token not in html for token in ("INLINE7", "NUM11", "SUB3", "DEN5", "NEXT8", "DELETED9", "INSERTED5"))
    print("PASS Office Math report coverage")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--make", type=Path)
    mode.add_argument("--verify", type=Path)
    args = parser.parse_args()
    make(args.make) if args.make else verify(args.verify)
