"""Outline a wordmark in Inter to an SVG path, so the lockups render without the font.

The result, wordmark.json beside the brand.json (by default the folder above this tool), is committed:
generate-logos.mjs reads it and never needs the font. Run this only when the wordmark, the font or its version changes.

    python -m venv .venv && .venv/Scripts/python -m pip install fonttools uharfbuzz
    .venv/Scripts/python <tool>/outline-wordmark.py <path/to/Inter-Bold.ttf> --text <Product> [--out <wordmark.json>]

A wordmark set in more than one weight or colour is given as runs, each with its font and the colour role that
paints it, optionally tracked (letter spacing after every glyph, in em) and with a gap between the runs (in em):

    .venv/Scripts/python <tool>/outline-wordmark.py --run <FIRST> <Inter-Regular.ttf> accent \
        --run <SECOND> <Inter-Bold.ttf> structure --tracking 0.0714286 --gap 0.0714286 [--out <wordmark.json>]

Its wordmark.json then holds "parts", one path per run with its role, instead of the single "d".

Inter comes from its official release (https://github.com/rsms/inter/releases, extras/ttf/Inter-*.ttf) under the
SIL Open Font License 1.1; the outlined glyphs are a use the licence permits (see README.md beside this file).

The text is shaped with HarfBuzz, so the font's kerning applies (e.g. the "Te" pair is kerned), across the boundary of
two neighbouring runs in the same font without a gap too. Coordinates are in font
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


def outline(font: Path, texts: list[str], x: float, tracking: float, bounds: BoundsPen | None):
    """Draws the runs `texts` in `font` from pen position x, shaped as one text, so the font's kerning applies across
    the runs' boundaries too; returns the font, one path per run, the bounds pen and the new pen position."""
    blob = hb.Blob.from_file_path(str(font))
    hbfont = hb.Font(hb.Face(blob))
    buf = hb.Buffer()
    buf.add_str("".join(texts))
    buf.guess_segment_properties()
    hb.shape(hbfont, buf, {"kern": True, "liga": True})

    tt = TTFont(str(font))
    glyphs = tt.getGlyphSet()
    order = tt.getGlyphOrder()
    track = tracking * tt["head"].unitsPerEm if tracking else 0
    pens = [SVGPathPen(glyphs, ntos=lambda v: f"{round(v, 1):g}") for _ in texts]
    # A glyph belongs to the run its cluster (the index of its first character) lies in.
    ends, end = [], 0
    for text in texts:
        end += len(text)
        ends.append(end)
    bounds = bounds or BoundsPen(glyphs)
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        gname = order[info.codepoint]
        run = next(i for i, e in enumerate(ends) if info.cluster < e)
        # y up in the font, y down in SVG: flip around the baseline.
        transform = (1, 0, 0, -1, x + pos.x_offset, -pos.y_offset)
        glyphs[gname].draw(TransformPen(pens[run], transform))
        glyphs[gname].draw(TransformPen(bounds, transform))
        x += pos.x_advance + track
    return tt, [pen.getCommands() for pen in pens], bounds, x


def describe(tt: TTFont) -> dict:
    name = tt["name"]
    return {"font": name.getDebugName(4), "fontVersion": name.getDebugName(5)}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("font", type=Path, nargs="?", help="the font of a wordmark in one run (with --text)")
    ap.add_argument("--text")
    ap.add_argument("--run", nargs=3, action="append", metavar=("TEXT", "FONT", "ROLE"),
                    help="a run of the wordmark in its font, painted by a colour role of brand.json; repeat in order")
    ap.add_argument("--tracking", type=float, default=0.0, help="letter spacing after every glyph, in em")
    ap.add_argument("--gap", type=float, default=0.0, help="extra space between two runs, in em")
    ap.add_argument("--out", type=Path, default=Path(__file__).resolve().parent.parent / "wordmark.json")
    args = ap.parse_args()
    if bool(args.run) == bool(args.font and args.text):
        ap.error("give either <font> --text <text> or one or more --run TEXT FONT ROLE")

    licence = "SIL Open Font License 1.1 (https://openfontlicense.org), (c) 2016 The Inter Project Authors"
    if not args.run:
        tt, (d,), bounds, x = outline(args.font, [args.text], 0, args.tracking, None)
        x_min, y_min, x_max, y_max = bounds.bounds
        out = {"text": args.text, **describe(tt), "licence": licence}
        if args.tracking:
            out["tracking"] = args.tracking
        out.update({
            "unitsPerEm": tt["head"].unitsPerEm,
            "capHeight": tt["OS/2"].sCapHeight,
            "xHeight": tt["OS/2"].sxHeight,
            "advance": x,
            "bbox": [x_min, y_min, x_max, y_max],
            "d": d,
        })
    else:
        # Neighbouring runs in the same font with no gap between them are shaped together (kerned across the
        # boundary, so a wordmark split only for its colours keeps the single run's geometry); any other boundary
        # is shaped apart, the gap after it.
        groups = []
        for text, font, role in args.run:
            same = groups and not args.gap and Path(font).resolve() == groups[-1][0].resolve()
            if same:
                groups[-1][1].append((text, role))
            else:
                groups.append((Path(font), [(text, role)]))
        x, bounds, parts, first = 0.0, None, [], None
        for i, (font, runs) in enumerate(groups):
            tt, ds, bounds, x = outline(font, [t for t, _ in runs], x, args.tracking, bounds)
            if first is None:
                first = tt
            elif tt["head"].unitsPerEm != first["head"].unitsPerEm:
                ap.error("the runs' fonts have different units per em")
            parts += [{"text": t, **describe(tt), "role": role, "d": d} for (t, role), d in zip(runs, ds)]
            if i < len(groups) - 1:
                x += args.gap * first["head"].unitsPerEm
        x_min, y_min, x_max, y_max = bounds.bounds
        out = {
            "text": "".join(p["text"] for p in parts),
            "licence": licence,
            "tracking": args.tracking,
            "gap": args.gap,
            "unitsPerEm": first["head"].unitsPerEm,
            "capHeight": first["OS/2"].sCapHeight,
            "xHeight": first["OS/2"].sxHeight,
            "advance": round(x, 1),
            "bbox": [round(v, 1) for v in (x_min, y_min, x_max, y_max)],
            "parts": parts,
        }
    args.out.write_text(json.dumps(out, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {args.out} ({out['text']}, advance {out['advance']}/{out['unitsPerEm']})")


if __name__ == "__main__":
    main()
