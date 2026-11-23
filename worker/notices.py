from pathlib import Path
import importlib.metadata as metadata
import shutil
import sys

target=Path("build/third-party")
target.mkdir(exist_ok=True)
for package in metadata.distributions():
    for file in package.files or ():
        name=str(file).lower()
        if ".." in file.parts:continue
        if not any(term in name for term in ("license","licence","copying","notice","copyright")):continue
        source=package.locate_file(file)
        if source.is_file() and source.suffix.lower() not in (".py",".pyc",".pyd"):
            out=target/package.metadata["Name"]/str(file)
            out.parent.mkdir(parents=True,exist_ok=True)
            shutil.copyfile(source,out)
shutil.copyfile(Path(sys.base_prefix)/"LICENSE.txt",target/"Python-LICENSE.txt")
apache=next(file for file in target.rglob("Apache-2.0.txt"))
shutil.copyfile(apache,target/"RapidOCR-and-PaddleOCR-Apache-2.0.txt")
(target/"OCR-NOTICE.txt").write_text("RapidOCR 1.4.4, RapidAI and SWHL, https://github.com/RapidAI/RapidOCR\nChinese PP-OCRv4 detector and recognizer, mobile orientation classifier: PaddleOCR authors, https://github.com/PaddlePaddle/PaddleOCR\nModels are the unmodified ONNX files distributed with rapidocr-onnxruntime. Both projects use Apache License 2.0.\n",encoding="utf-8")
