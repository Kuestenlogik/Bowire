#!/usr/bin/env python3
"""Build packaging/macos/Bowire.app/Contents/Resources/bowire.icns (#685).

macOS wants an .icns for an app bundle's icon, and `iconutil` only exists on
macOS -- but the release pipeline cross-publishes the osx-* artefacts on an
Ubuntu runner. Since macOS 10.7 an .icns is just a container of PNG entries,
so it can be written anywhere.

Container layout:

    'icns'  <uint32 be: total length of the whole file>
    then, repeated:
        <4-char type>  <uint32 be: 8 + len(payload)>  <payload>

The types below are the PNG-based ones. Each names an intended pixel size;
Finder scales between them.

Run from the repo root:

    python scripts/packaging/make-icns.py

Source of truth is images/bowire_logo_small.png. Re-run this after changing
the logo and commit the result -- the .icns is checked in so the app bundle
in packaging/macos/ is complete on its own.
"""

from __future__ import annotations

import io
import struct
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:  # pragma: no cover - the message is the whole point
    sys.exit("Pillow is required: python -m pip install pillow")

# type code -> pixel size.
#
# Deliberately capped at the source resolution (256). macOS also defines
# 512 and 1024 entries for HiDPI, and writing them from a 256 source would
# just be an upscale -- four times the bytes for detail that is not there,
# and Finder produces the identical result by scaling the 256 itself. Add
# ic09 / ic10 / ic13 / ic14 here the day images/ carries a larger logo.
ENTRIES = {
    "ic11": 32,
    "ic12": 64,
    "ic07": 128,
    "ic08": 256,
}

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "images" / "bowire_logo_small.png"
TARGET = REPO / "packaging" / "macos" / "Bowire.app" / "Contents" / "Resources" / "bowire.icns"


def png_bytes(image: "Image.Image", size: int) -> bytes:
    """One square PNG at `size`, RGBA, no metadata."""
    # Every entry is at or below the source resolution, so this only ever
    # downscales.
    resized = image.resize((size, size), Image.Resampling.LANCZOS)
    buffer = io.BytesIO()
    resized.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def main() -> int:
    if not SOURCE.is_file():
        sys.exit(f"source icon missing: {SOURCE}")

    source = Image.open(SOURCE).convert("RGBA")
    if source.width != source.height:
        sys.exit(f"source icon must be square, got {source.width}x{source.height}")
    biggest = max(ENTRIES.values())
    if source.width < biggest:
        sys.exit(f"source icon is {source.width}px but ENTRIES asks for {biggest}px; "
                 "shrink ENTRIES or supply a larger logo rather than upscaling")

    chunks = []
    for code, size in ENTRIES.items():
        payload = png_bytes(source, size)
        chunks.append(code.encode("ascii") + struct.pack(">I", 8 + len(payload)) + payload)

    body = b"".join(chunks)
    icns = b"icns" + struct.pack(">I", 8 + len(body)) + body

    TARGET.parent.mkdir(parents=True, exist_ok=True)
    TARGET.write_bytes(icns)
    print(f"{TARGET.relative_to(REPO)}  {len(icns)} bytes, {len(ENTRIES)} entries")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
