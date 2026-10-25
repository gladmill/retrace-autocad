#!/usr/bin/env python3
"""Score wireframe SVGs against real Windows captures (<Project>__<DIALOG>.png + .json).

Usage: compare_captures.py CAPTURES_DIR OUT_DIR [--overlay DIR] [--only SUBSTR]
  OUT_DIR is a Retrace output folder (sourcemap.json + assets/). SVGs are rasterized 1:1 with
  rsvg-convert and aligned at (0,0). Score = fraction of pixels (over the union canvas) whose gray value
  differs by more than 40. Text anti-aliasing never matches exactly, so use it to compare before/after.
Needs Pillow and rsvg-convert. The capture folder is data: never commit it.
"""
import glob
import json
import os
import subprocess
import sys
import tempfile

from PIL import Image, ImageChops, ImageOps


def score(cap_png, svg_png):
    a = Image.open(cap_png).convert("RGB")
    b = Image.open(svg_png).convert("RGB")
    w, h = max(a.width, b.width), max(a.height, b.height)
    pa = Image.new("RGB", (w, h), (255, 255, 255)); pa.paste(a)
    pb = Image.new("RGB", (w, h), (255, 255, 255)); pb.paste(b)
    d = ImageChops.difference(ImageOps.grayscale(pa), ImageOps.grayscale(pb))
    bad = sum(1 for v in d.getdata() if v > 40)
    return bad / (w * h), a.size, b.size, pa, pb


def overlay(pa, pb, path):
    ga, gb = ImageOps.grayscale(pa), ImageOps.grayscale(pb)
    ov = Image.merge("RGB", (gb, ga, ga))  # red = Windows only, cyan = SVG only
    w, h = pa.size
    c = Image.new("RGB", (w * 3 + 20, h), "white")
    c.paste(pa, (0, 0)); c.paste(pb, (w + 10, 0)); c.paste(ov, (2 * w + 20, 0))
    c.resize((c.width * 2, c.height * 2), Image.NEAREST).save(path)


def main():
    args = sys.argv[1:]
    caps, out = args[0], args[1]
    ov_dir = args[args.index("--overlay") + 1] if "--overlay" in args else None
    only = args[args.index("--only") + 1] if "--only" in args else ""
    sm = json.load(open(os.path.join(out, "sourcemap.json")))
    asset = {e["id"][5:]: e["asset"] for e in sm["entries"] if e["id"].startswith("form:") and e.get("asset")}
    tot = []
    for jf in sorted(glob.glob(os.path.join(caps, "*.json"))):
        j = json.load(open(jf))
        fid = j["formId"]
        if only and only not in fid:
            continue
        svg = asset.get(fid)
        if not svg:
            print(f"{fid:55s} no svg"); continue
        png = tempfile.mktemp(suffix=".png")
        subprocess.run(["rsvg-convert", "-o", png, os.path.join(out, svg)], check=True)
        s, sa, sb, pa, pb = score(jf[:-5] + ".png", png)
        os.remove(png)
        tot.append(s)
        print(f"{fid:55s} {s:7.4f} cap={sa[0]}x{sa[1]} svg={sb[0]}x{sb[1]} {'OK' if sa == sb else 'SIZE'}")
        if ov_dir:
            os.makedirs(ov_dir, exist_ok=True)
            overlay(pa, pb, os.path.join(ov_dir, fid + ".png"))
    if tot:
        print(f"mean {sum(tot) / len(tot):.4f} over {len(tot)}")


main()
