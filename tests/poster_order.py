"""Check column reading order on synthetic OCR line boxes, without source images."""
import sys
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"worker"))
from review import column_order, poster_gutter


def row(x1, y1, x2, text):
    return ([(x1,y1),(x2,y1),(x2,y1+15),(x1,y1+15)],text,.99)


def read(rows):
    return [r[1] for r in column_order(rows,800,poster_gutter(rows,800))]


for left,right in ((150,440),(220,504),(100,335)):
    rows=[row(100,0,720,"heading"),row(left,20,right-64,"L1"),row(right,21,right+150,"R1"),
          row(left,42,right-55,"L2"),row(right,42,right+135,"R2"),row(100,65,715,"footer")]
    assert read(rows)==["heading","L1","L2","R1","R2","footer"]

single=[row(75,i*25,625-i*40,str(i)) for i in range(6)]
assert read(single)==[r[1] for r in single]
print("PASS centered and offset columns, spanning headings, single-column text")
