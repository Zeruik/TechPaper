from pathlib import Path
import argparse
import hashlib
import re
import subprocess
import zipfile
from xml.etree import ElementTree as E
from io import BytesIO

def main():
    parser=argparse.ArgumentParser(description="Local release audit. Prints counts only.")
    parser.add_argument("--private",type=Path,required=True)
    parser.add_argument("--install",type=Path)
    args=parser.parse_args()
    source=args.private.read_bytes()
    with zipfile.ZipFile(BytesIO(source)) as z:
        root=E.fromstring(z.read("word/document.xml"))
        ns="{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
        paragraphs=["".join(n.text or "" for n in p.iter(ns+"t")).strip() for p in root.iter(ns+"p")]
    needles={args.private.stem,*(p[:56] for p in paragraphs if len(p)>=56)}
    encoded={a.encode(enc) for a in needles for enc in ("utf-8","utf-16-le")}
    private_hash=hashlib.sha256(source).digest()
    seen=set()
    def scan(data,depth=0):
        key=hashlib.sha256(data).digest()
        if key in seen:return
        seen.add(key)
        if key==private_hash or any(w in data for w in encoded):raise SystemExit("Private material match; stop publication")
        if depth<3 and data[:4]==b"PK\x03\x04":
            with zipfile.ZipFile(BytesIO(data)) as z:
                for e in z.infolist():
                    if e.file_size:scan(z.read(e),depth+1)
    def git(*args):return subprocess.check_output(["git",*args])
    def checkpath(name):
        if not re.fullmatch(r"(?:\.gitignore|README\.md|LICENSE|build(?:-worker)?\.ps1|installer\.iss|assets/(?:logo\.png|workspace\.png|TechPaper\.ico|manuscript-review-icon\.svg)|docs/(?:TERMS\.txt|RESEARCH\.md|VALIDATION\.md)|src/[A-Za-z]+\.cs|src/app\.manifest|tests/(?:sample\.docx|[A-Za-z_]+\.py|[A-Za-z]+\.cs)|worker/(?:review\.py|notices\.py|requirements\.lock))",name):raise SystemExit("Unapproved Git path")
    for line in git("ls-files","-s","-z").split(b"\0"):
        if line:
            attrs,name=line.split(b"\t",1);checkpath(name.decode());scan(git("cat-file","blob",attrs.split()[1].decode()))
    for line in git("rev-list","--objects","--all").splitlines():
        oid,_,name=line.partition(b" ")
        if name and git("cat-file","-t",oid).strip()==b"blob":
            checkpath(name.decode());scan(git("cat-file","blob",oid))
    if args.install:
        for p in args.install.rglob("*"):
            if p.is_file():
                if p.suffix.lower() in (".docx",".pdf",".pptx",".log") and p.relative_to(args.install).as_posix()!="tests/sample.docx":raise SystemExit("Unexpected document/log in installer")
                scan(p.read_bytes())
        if hashlib.sha256((args.install/"tests/sample.docx").read_bytes()).digest()!=hashlib.sha256(Path("tests/sample.docx").read_bytes()).digest():raise SystemExit("Sample mismatch")
    print("PASS staged/tracked/history and optional installed payload; distinct contents checked:",len(seen))
if __name__=="__main__":main()
