"""Generate paired local engineering controls and score them without retuning."""
import argparse
from collections import Counter
import json
from pathlib import Path
import sys
from urllib.request import build_opener, HTTPRedirectHandler, ProxyHandler, Request

TOPICS = [
    ("称量前后检查天平零点", "checking a scale's zero reading before and after weighing"),
    ("改变夹具位置后重复测量梁的挠度", "repeating beam deflection measurements after moving the clamp"),
    ("在两种照明条件下读取同一刻度尺", "reading the same ruler under two lighting conditions"),
    ("用计时器记录同一电机多次启动的时间", "timing repeated starts of the same electric motor"),
]
MODELS = {
    "qwen2.5:3b": "357c53fb659c5076de1d65ccb0b397446227b71a42be9d1603d46168015c9e4b",
    "qwen3.5:4b": "d8b0f5e9760cd1682034f292d7ef72ec46f432149be0df7574bf2d6e92e38c04",
}
LANGUAGES = ("zh", "en")


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def generate(folder):
    http = build_opener(ProxyHandler({}), NoRedirect())
    base = "http://127.0.0.1:11434/api/"
    def call(path, data=None):
        body = None if data is None else json.dumps(data).encode()
        req = Request(base+path, body, {"Content-Type":"application/json"})
        with http.open(req, timeout=180) as response:
            return json.load(response)
    tags = {m["name"]:m for m in call("tags")["models"]}
    folder.mkdir(parents=True, exist_ok=True)
    for model, digest in MODELS.items():
        entry = tags[model]
        assert entry["digest"]==digest and not entry.get("remote_model") and not entry.get("remote_host")
        info = call("show", {"model":model+":local"})
        assert info.get("model_info") and not info.get("remote_model") and not info.get("remote_host")
        for i, topics in enumerate(TOPICS):
            for lang, topic in zip(LANGUAGES, topics):
                prompt = (f"请用现代中文写一段独立的工程实验方法摘要，主题为{topic}。"
                          "约100至150个汉字，说明步骤、观测对象和一项局限。不要编造结果，不要加标题。"
                          if lang=="zh" else
                          f"Write one standalone engineering methods abstract on {topic}. "
                          "Use about 70 to 100 English words. Describe the procedure, what is observed and one limitation. "
                          "Do not invent results or add a title.")
                target = folder/f"{digest[:12]}-{i}-{lang}.json"
                if target.exists():
                    raise FileExistsError("Use an empty output directory to preserve the original run")
                reply = call("chat", {"model":model+":local","stream":False,"think":False,
                                     "messages":[{"role":"user","content":prompt}],
                                     "options":{"seed":173,"temperature":0,"num_ctx":2048,"num_predict":512}})
                record = {"model":model,"digest":digest,"topic":i,"language":lang,"prompt":prompt,
                          "text":reply["message"]["content"],"finished":reply.get("done_reason")}
                with target.open("x", encoding="utf8") as f:
                    json.dump(record, f, ensure_ascii=False)
                print(model, i, lang, record["finished"], flush=True)


def evaluate(folder, paths):
    import onnxruntime as ort
    from tokenizers import Tokenizer
    from onnx_benchmark import balanced_windows, window_label
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]/"worker"))
    from review import SENT, evidence
    for lang, path in zip(LANGUAGES, paths):
        labels = json.loads((path.parent/"config.json").read_bytes())["id2label"]
        assert set(labels.values())=={"Human","ChatGPT"}
        tok = Tokenizer.from_file(str(path.parent/"tokenizer.json"))
        tok.no_padding()
        tok.no_truncation()
        options = ort.SessionOptions()
        options.intra_op_num_threads = 3
        session = ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])
        for model, digest in MODELS.items():
            candidate, baseline = Counter(), Counter()
            for i in range(len(TOPICS)):
                row = json.loads((folder/f"{digest[:12]}-{i}-{lang}.json").read_bytes())
                assert (row["model"],row["digest"],row["topic"],row["language"])==(model,digest,i,lang)
                text = row["text"].strip()
                if not text or row["finished"]!="stop":
                    candidate["incomplete"]+=1
                    baseline["incomplete"]+=1
                    continue
                votes = {window_label(session, w, labels) for w in balanced_windows(tok.encode(text))}
                candidate[votes.pop() if len(votes)==1 else "abstain"]+=1
                signals = {evidence(m.group())[0] for m in SENT.finditer(text) if m.group().strip()}
                decision = next((k for k in ("AI","MIX","HUMAN") if k in signals),"abstain")
                baseline[decision]+=1
            print(lang,model,"candidate",dict(candidate),"baseline",dict(baseline))


if __name__=="__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--generate", action="store_true")
    modes.add_argument("--score", nargs=2, type=Path, metavar=("ZH_ONNX","EN_ONNX"))
    args = parser.parse_args()
    if args.generate:
        generate(args.folder)
    else:
        evaluate(args.folder, args.score)
