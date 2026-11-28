"""Compare perspective on paired, known-generated engineering notes locally."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import sys
from urllib.request import ProxyHandler, Request, build_opener

from generator_holdout import MODELS, NoRedirect

CASES = [
    ("天平零点为 0.02 g", "the balance zero reading was 0.02 g"),
    ("夹具移动后梁的挠度为 1.4 mm", "beam deflection was 1.4 mm after the clamp moved"),
    ("刻度尺在室内光照下的读数为 15 mm", "the ruler reading was 15 mm under room lighting"),
    ("电机启动耗时 0.8 s", "the motor took 0.8 s to start"),
]
MODEL = "qwen2.5:3b"


def generate(folder):
    folder.mkdir(parents=True, exist_ok=False)
    http = build_opener(ProxyHandler({}), NoRedirect())

    def call(route, data=None):
        body = None if data is None else json.dumps(data).encode()
        request = Request("http://127.0.0.1:11434/api/" + route, body, {"Content-Type": "application/json"})
        with http.open(request, timeout=120) as response:
            return json.load(response)

    tag = next(m for m in call("tags")["models"] if m["name"] == MODEL)
    selector = MODEL + ":local"
    info = call("show", {"model": selector})
    if (tag["digest"] != MODELS[MODEL] or not info.get("model_info") or
            any(row.get(k) for row in (tag, info) for k in ("remote_model", "remote_host"))):
        raise ValueError("Unexpected local weights")
    for i, (zh, en) in enumerate(CASES):
        for lang in ("zh", "en"):
            for voice in ("personal", "impersonal"):
                person = voice == "personal"
                prompt = (f"用现代中文写一句实验记录，保留这条虚构记录中的数值和单位：{zh}。"
                          + ("从实验者单数第一人称角度叙述。" if person else "用无人称客观表述。")
                          if lang == "zh" else
                          f"Write one English laboratory notebook sentence using this fictional observation: {en}. "
                          + ("Use the operator's first-person singular perspective." if person else "Use impersonal wording."))
                reply = call("chat", {"model": selector, "stream": False, "think": False,
                             "options": {"seed": 173, "temperature": 0, "num_predict": 128},
                             "messages": [{"role": "user", "content": prompt}]})
                record = {"digest": tag["digest"], "prompt": prompt, "text": reply["message"]["content"],
                          "finished": reply.get("done_reason")}
                (folder / f"{i}-{lang}-{voice}.json").write_text(json.dumps(record), encoding="utf8")


def evaluate(folder):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
    from review import SENT, evidence
    groups = defaultdict(Counter)
    for i in range(len(CASES)):
        for lang in ("zh", "en"):
            for voice in ("personal", "impersonal"):
                row = json.loads((folder / f"{i}-{lang}-{voice}.json").read_bytes())
                if row["digest"] != MODELS[MODEL] or row["finished"] != "stop" or not row["text"].strip():
                    raise ValueError("Incomplete control")
                labels = {evidence(m.group())[0] for m in SENT.finditer(row["text"]) if m.group().strip()}
                counts = groups[lang, voice]
                counts.update(labels)
                counts["all_abstained"] += labels == {"未判断"}
                counts["documents"] += 1
    print("language voice documents any_AI any_MIX any_HUMAN all_abstained")
    for group, counts in sorted(groups.items()):
        print(*group, *(counts[k] for k in ("documents", "AI", "MIX", "HUMAN", "all_abstained")))


if __name__ == "__main__":
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("folder", type=Path)
    cli.add_argument("--generate", action="store_true", help="Create new controls before scoring")
    args = cli.parse_args()
    if args.generate:
        generate(args.folder)
    evaluate(args.folder)
