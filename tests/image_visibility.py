"""Compare visible OCR text and display-oriented pixel boxes on synthetic image exports."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
from tempfile import TemporaryDirectory

from PIL import Image, ImageDraw, ImageFont


def images(folder, poster):
    transforms = (None, Image.Transpose.FLIP_LEFT_RIGHT, Image.Transpose.ROTATE_180,
                  Image.Transpose.FLIP_TOP_BOTTOM, Image.Transpose.TRANSPOSE,
                  Image.Transpose.ROTATE_90, Image.Transpose.TRANSVERSE, Image.Transpose.ROTATE_270)
    for orientation, transform in enumerate(transforms, 1):
        frame = poster.transpose(transform) if transform is not None else poster
        exif = Image.Exif()
        exif[274] = orientation
        path = folder / f"orientation-{orientation}.jpg"
        frame.save(path, quality=98, exif=exif)
        yield path, True
    frame = Image.new("RGBA", (poster.width, poster.height+100), "white")
    frame.paste(poster, (0, 0))
    draw = ImageDraw.Draw(frame)
    draw.text((64, poster.height), "Hidden 0.35 mm", font=ImageFont.truetype("arial.ttf", 48), fill="black")
    alpha = frame.getchannel("A")
    alpha.paste(0, (0, poster.height, frame.width, frame.height))
    frame.putalpha(alpha)
    for mode in ("RGBA", "LA"):
        path = folder / (mode+".png")
        frame.convert(mode).save(path)
        yield path, True
    faded = frame.copy()
    faded.putalpha(frame.getchannel("A").point(lambda v: round(v*.75)))
    path = folder / "translucent.png"
    faded.save(path)
    yield path, True
    hidden = frame.crop((0, poster.height, frame.width, frame.height)).convert("RGB").quantize(colors=2)
    path = folder / "palette.png"
    hidden.save(path, transparency=bytes([0, 0]))
    yield path, False


def verify(fixtures, worker):
    with Image.open(fixtures/"poster.png") as image:
        poster = image.convert("RGB")
    expected = json.loads((fixtures/"expected.json").read_bytes())
    command = [sys.executable, worker] if worker.suffix==".py" else [worker]
    failures = []
    with TemporaryDirectory() as temp:
        for path, has_text in images(Path(temp), poster):
            output = path.with_suffix(".html")
            subprocess.run([*command, path, output], check=True, stdout=subprocess.DEVNULL)
            from bilingual_report import Highlights
            parser = Highlights()
            parser.feed(output.read_text(encoding="utf-8"))
            parser.close()
            targets = expected if has_text else []
            try:
                assert len(parser.rows)==len(targets), f"expected {len(targets)} lines, got {len(parser.rows)}"
                for (location, text, _), (x, y, original) in zip(parser.rows, targets):
                    assert "".join(text.split())=="".join(original.split()), "visible text or reading order"
                    coords = list(map(int, re.findall(r"-?\d+",location.split(" / px ")[1].split(" ·")[0])))
                    assert len(coords)==8 and abs(min(coords[::2])/2-x)<8 and y-26<min(coords[1::2])/2<y, "pixel box"
                print("PASS",path.name, len(targets),"lines")
            except AssertionError as error:
                failures.append(f"{path.name}: {error}")
    if failures:
        raise AssertionError(", ".join(failures))


if __name__=="__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixtures", type=Path)
    parser.add_argument("worker", type=Path)
    args = parser.parse_args()
    verify(args.fixtures, args.worker.resolve())
