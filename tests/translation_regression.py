"""Reproduce aggregate counts without redistributing the research corpus."""
from pathlib import Path
import collections
import json
import sys
import zipfile
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"worker"))
from review import SENT, evidence


def check_numeric_sentence_boundary():
    first = "I recorded the temperature as 22.5."
    second = " In conclusion, it is worth noting that room conditions varied."
    matches = list(SENT.finditer(first + second))
    assert [(m.start(), m.end(), m.group(), evidence(m.group())[0]) for m in matches] == [
        (0, len(first), first, "HUMAN"),
        (len(first), len(first + second), second, "AI"),
    ]

def tally(texts):
    scores=collections.Counter(evidence(t)[0] for t in texts)
    return [scores[x] for x in ("AI","MIX","HUMAN","未判断")]

if __name__=="__main__":
    check_numeric_sentence_boundary()
    with zipfile.ZipFile(sys.argv[1]) as data:
        for suffix in ("Reference-HT.txt","Reference-PE.txt","Combo-4.txt"):
            name=next(n for n in data.namelist() if n.endswith(suffix))
            lines=data.read(name).decode("utf-8-sig").splitlines()
            print(suffix,len(lines),tally(lines),"first100",tally(lines[:100]))
    rows=json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))["rows"]
    print("WMT Chinese first100",len(rows),tally(x["row"]["translation"]["zh"] for x in rows))
    controls={
        "Synthetic AI":["值得注意的是，本次测量对后续估计至关重要。","样本在室内放置两天，再测量质量。","I recorded 15 readings before the second trial."],
        "Scientific synthetic before":["值得注意的是，两份样本的初始温度对读数至关重要。"],
        "Scientific synthetic after":["两份样本的初始温度对读数至关重要。"],
        "Synthetic grammar change":["I recorded 15 reading before the second trial."],
        "Synthetic short":["读数为 15。"],
        "Parallel/repeated sentence":["读数相同。读数相同。读数相同。读数相同。"],
        "Unequal sentence lengths":["读数相同。为了检查读数是否受室内温度影响，我们在门窗关闭后重复了测量。"],
    }
    for cohort,texts in controls.items():print(cohort,len(texts),tally(texts))
    if len(sys.argv)>3:
        with zipfile.ZipFile(sys.argv[3]) as data:
            for cohort in ("TOEFL_real_91","HewlettStudentEssay_real_88","TOEFL_gpt4polished_91","CS224N_gpt3_145"):
                from review import SENT
                name=next(n for n in data.namelist() if n.endswith("/"+cohort+"/data.json"))
                docs=json.loads(data.read(name));counts=collections.Counter();flagged=0
                for doc in docs:
                    labels=[evidence(m.group())[0] for m in SENT.finditer(doc["document"]) if m.group().strip()]
                    counts.update(labels);flagged+=any(k in ("AI","MIX") for k in labels)
                print(cohort,"documents",len(docs),"any AI/MIX",flagged,"segments",[counts[k] for k in ("AI","MIX","HUMAN","未判断")])
