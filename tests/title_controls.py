"""Create local, same-title synthetic controls for the external bilingual holdout."""
import argparse
import json
from pathlib import Path
import re
from urllib.request import build_opener, ProxyHandler, Request
from xml.etree import ElementTree as ET

from generator_holdout import MODELS, NoRedirect


MODEL = "qwen2.5:3b"


def read_control(folder, paper, lang):
    row=json.loads((folder/f"{paper}-{lang}.json").read_bytes())
    if (row["paper"],row["lang"],row["model"],row["digest"],row["finished"]) != (paper,lang,MODEL,MODELS[MODEL],"stop"):
        raise ValueError("Title control provenance mismatch")
    text=row["text"].strip()
    if not text:
        raise ValueError("Empty title control")
    return text


def generate(folder, output, papers):
    http = build_opener(ProxyHandler({}), NoRedirect())
    base = "http://127.0.0.1:11434/api/"
    def call(endpoint, data=None):
        body = None if data is None else json.dumps(data).encode()
        with http.open(Request(base+endpoint, body, {"Content-Type":"application/json"}),timeout=240) as response:
            return json.load(response)
    entry, = [m for m in call("tags")["models"] if m["name"]==MODEL]
    info = call("show", {"model":MODEL+":local"})
    if entry["digest"] != MODELS[MODEL] or not info.get("model_info") or any(
        m.get(k) for m in (entry,info) for k in ("remote_model","remote_host")
    ):
        raise ValueError("Local model mismatch")
    output.mkdir(exist_ok=False)
    for paper in papers:
        meta=ET.parse(folder/(paper+".xml")).find("./front/article-meta")
        for lang,title_tag,abstract_tag in (("zh","article-title","abstract"),("en","trans-title-group/trans-title","trans-abstract")):
            title=" ".join("".join(meta.find("title-group/"+title_tag).itertext()).split())
            abstract=" ".join("".join(p.itertext()) for p in meta.find(abstract_tag).iter("p"))
            length=len(re.findall(r"[\u3400-\u9fff]",abstract)) if lang=="zh" else len(abstract.split())
            if not title or length<20:
                raise ValueError("Missing source metadata")
            prompt=(f"题名：{title}\n为此题名独立写一段约 {length} 个汉字的方法研究摘要。使用现代中文，不编造实测结果或引用，只输出摘要。"
                    if lang=="zh" else f"Title: {title}\nWrite one independent methods abstract of about {length} English words on this topic. Do not invent measured results or citations. Return only the abstract.")
            r=call("chat",{"model":MODEL+":local","stream":False,"think":False,
                          "messages":[{"role":"user","content":prompt}],
                          "options":{"seed":173,"temperature":0,"num_ctx":8192,"num_predict":4096}})
            record=dict(paper=paper,lang=lang,model=MODEL,digest=entry["digest"],target_length=length,
                        finished=r.get("done_reason"),text=r["message"]["content"].strip())
            with (output/f"{paper}-{lang}.json").open("x",encoding="utf-8") as dest:
                json.dump(record,dest,ensure_ascii=False)
            print(paper,lang,length,record["finished"],flush=True)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder",type=Path)
    parser.add_argument("output",type=Path)
    parser.add_argument("papers",nargs="+")
    generate(**vars(parser.parse_args()))
