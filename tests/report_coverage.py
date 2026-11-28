"""Exercise the HTML report with controlled OCR confidence and distinct locations."""
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
import review


def main():
    template = "综上所述，准确识别非空文本的边界至关重要。"
    duplicate = "I recorded 3 readings during calibration."
    rows = [
        ("A", template, "OCR", 0.749),
        ("B", template, "OCR", 0.750),
        ("C", duplicate, "OCR", 0.91),
        ("D", duplicate, "OCR", 0.91),
        ("E", "In conclusion, I measured 5 samples.", "文字层", None),
        ("F", "短句。", "文字层", None),
        ("G", "", "图像跳过", None),
    ]
    with TemporaryDirectory() as tmp, patch.object(review, "extract", return_value=rows):
        output = Path(tmp) / "report.html"
        review.report(Path("synthetic.pdf"), output)
        page = output.read_text(encoding="utf8")
    assert "共 6 个非空片段，分类 4（66.7%），弃答 1，低置信未分析 1，空白/跳过/失败位置 1。" in page
    assert "AI 1 · MIX 1 · HUMAN 2" in page
    assert "OCR &lt; 0.75" in page and "OCR 0.75</small>" in page
    print("PASS report coverage, repeated observations and OCR cutoff")


if __name__ == "__main__":
    main()
