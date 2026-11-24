"""Group frozen bilingual JATS abstracts and independently generated controls for local QA."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
from xml.etree import ElementTree

ARTICLES = ("PMC4764004", "PMC4858505", "PMC5434299", "PMC5579459", "PMC5972988", "PMC6000637")
LANG = "{http://www.w3.org/XML/1998/namespace}lang"


def collect(folder):
    groups = defaultdict(list)
    for accession in ARTICLES:
        article = ElementTree.parse(folder / (accession + ".xml")).getroot()
        meta = article.find("./front/article-meta")
        if accession.removeprefix("PMC") not in {
            node.text for node in meta.findall("article-id") if node.get("pub-id-type") in ("pmc", "pmcid")
        } and accession not in {node.text for node in meta.findall("article-id")}:
            raise ValueError(f"Source identifier mismatch for {accession}")
        languages = set()
        for tag, role in (("abstract", "primary"), ("trans-abstract", "translation")):
            abstract = meta.find(tag)
            language = abstract.get(LANG, article.get(LANG))
            paragraphs = []
            for child in abstract:
                heading = (child.findtext("title") or "").strip().casefold()
                if heading in {"中文全文", "关键词", "keywords", "full text"}:
                    continue
                paragraphs.extend(" ".join("".join(p.itertext()).split()) for p in child.iter("p"))
            if not paragraphs or language not in {"en", "zh"} or language in languages:
                raise ValueError(f"Unexpected abstract metadata in {accession}")
            groups[language + " " + role].append("\n".join(paragraphs))
            languages.add(language)
        for language in ("en", "zh"):
            generated = (folder / f"{accession}-{language}.txt").read_text(encoding="utf-8").strip()
            han = sum("\u4e00" <= char <= "\u9fff" for char in generated)
            if not generated or (language == "zh") != (han > len(generated) * .3):
                raise ValueError(f"Check generated language in {accession}-{language}")
            groups[language + " generated"].append(generated)
    return groups


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("output", type=Path, help="Local JSON for onnx_benchmark.py, contains full texts")
    parser.add_argument("--baseline", action="store_true", help="Also print installed-rule document counts")
    args = parser.parse_args()
    groups = collect(args.folder)
    args.output.write_text(json.dumps(groups, ensure_ascii=False), encoding="utf-8")
    if args.baseline:
        import sys
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
        from review import SENT, evidence
        for cohort, texts in groups.items():
            labels = [{evidence(m.group())[0] for m in SENT.finditer(t) if m.group().strip()} for t in texts]
            print(cohort, len(texts), "any AI", sum("AI" in row for row in labels),
                  "any AI/MIX", sum(bool(row & {"AI", "MIX"}) for row in labels),
                  "all abstained", sum(row == {"未判断"} for row in labels))
