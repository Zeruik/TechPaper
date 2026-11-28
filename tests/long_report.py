import re
import sys
import subprocess
import zipfile
from pathlib import Path
from tempfile import TemporaryDirectory
from xml.sax.saxutils import escape
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"worker"))
import review

def main():
    with TemporaryDirectory() as folder:
        root=Path(folder);source=root/"长文 重复句.docx";out=root/"报告.html"
        phrase="我记录了 15 组读数，并对每个读数进行了独立复核。"
        with zipfile.ZipFile("tests/sample.docx") as orig,zipfile.ZipFile(source,"w",zipfile.ZIP_DEFLATED) as z:
            for part in orig.namelist():
                blob=orig.read(part)
                if part=="word/document.xml":
                    blob=('<w:document xmlns:w="'+review.W[1:-1]+'"><w:body>'+('<w:p><w:r><w:t>'+phrase+'</w:t></w:r></w:p>')*12000+
                          '<w:p><w:r><w:t>'+escape('& <script> 边界验证。')+'</w:t></w:r></w:p></w:body></w:document>').encode()
                z.writestr(part,blob)
        regression=Path("build/Regression.exe")
        if regression.is_file():
            subprocess.run([str(regression.resolve()),str(source),str(root/"重开.docx")],check=True)
        corpus=root/"本地来源.txt";corpus.write_text(phrase,encoding="utf-8")
        review.report(source,out,corpus)
        text=out.read_text(encoding="utf-8")
        assert text.count('class=\'HUMAN\'')==12000
        source_hit=f"本地来源.txt · 语料字符 0–{len(phrase)} · 段内字符 0–{len(phrase)} · {phrase}"
        assert text.count(source_hit)==12000
        assert "id='p12000s0'><small>document.xml / 段 12000 " in text
        assert "id='p12001s0'><small>document.xml / 段 12001 " in text
        assert '&lt;script&gt;' in text and len(set(re.findall("id='(p[0-9]+s[0-9]+)'",text)))==text.count("<mark")
        try:review.report(source,out)
        except FileExistsError:pass
        else:raise AssertionError("overwritten")
        print("PASS 12000 repeated paragraphs, stable spans, corpus provenance, escaping, no overwrite")
if __name__=="__main__":main()
