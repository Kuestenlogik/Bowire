"""Outline a product's wordmark in Inter Bold to an SVG path, so the lockups render without the font.

The result, wordmark.json beside the product's brand.json (by default the folder above this tool), is committed:
generate-logos.mjs reads it and never needs the font. Run this only when the wordmark, the font or its version changes.

    python -m venv .venv && .venv/Scripts/python -m pip install fonttools uharfbuzz
    .venv/Scripts/python <tool>/outline-wordmark.py <path/to/Inter-Bold.ttf> --text <Product> [--out <wordmark.json>]

Inter comes from its official release (https://github.com/rsms/inter/releases, extras/ttf/Inter-Bold.ttf) under the
SIL Open Font License 1.1; the outlined glyphs are a use the licence permits (see README.md beside this file).

The text is shaped with HarfBuzz, so the font's kerning applies (e.g. the "Te" pair is kerned). Coordinates are in font
units, y pointing down, the baseline at y = 0, the pen starting at x = 0.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import uharfbuzz as hb
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.boundsPen import BoundsPen
from fontTools.ttLib import TTFont


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("font", type=Path)
    ap.add_argument("--text", required=True)
    ap.add_argument("--out", type=Path, default=Path(__file__).resolve().parent.parent / "wordmark.json")
    args = ap.parse_args()

    blob = hb.Blob.from_file_path(str(args.font))
    face = hb.Face(blob)
    hbfont = hb.Font(face)
    buf = hb.Buffer()
    buf.add_str(args.text)
    buf.guess_segment_properties()
    hb.shape(hbfont, buf, {"kern": True, "liga": True})

    tt = TTFont(str(args.font))
    glyphs = tt.getGlyphSet()
    order = tt.getGlyphOrder()
    upem = tt["head"].unitsPerEm
    os2 = tt["OS/2"]
    name = tt["name"]

    svg_pen = SVGPathPen(glyphs, ntos=lambda v: f"{round(v, 1):g}")
    bounds = BoundsPen(glyphs)
    x = 0
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        gname = order[info.codepoint]
        # y up in the font, y down in SVG: flip around the baseline.
        transform = (1, 0, 0, -1, x + pos.x_offset, -pos.y_offset)
        glyphs[gname].draw(TransformPen(svg_pen, transform))
        glyphs[gname].draw(TransformPen(bounds, transform))
        x += pos.x_advance

    x_min, y_min, x_max, y_max = bounds.bounds
    out = {
        "text": args.text,
        "font": name.getDebugName(4),
        "fontVersion": name.getDebugName(5),
        "licence": "SIL Open Font License 1.1 (https://openfontlicense.org), (c) 2016 The Inter Project Authors",
        "unitsPerEm": upem,
        "capHeight": os2.sCapHeight,
        "xHeight": os2.sxHeight,
        "advance": x,
        "bbox": [x_min, y_min, x_max, y_max],
        "d": svg_pen.getCommands(),
    }
    args.out.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.out} ({out['font']}, {out['fontVersion']}, advance {x}/{upem})")


if __name__ == "__main__":
    main()
