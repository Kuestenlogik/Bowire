# Bowire branding

Bowire's logo files come from the shared Küstenlogik branding tool, the same generator every product uses. This
folder holds what is Bowire's own; the tool itself is a pinned copy in `tool/` (`tool/README.md` explains it in full).

| File | What it is |
|---|---|
| `bowire_mark_source.svg` | The owner's drawing of the mark, the "bw" (Inkscape). The main logo. |
| `bowire_emblem_source.svg` | The owner's drawing of the emblem: the mark in its ring with the bowline knot. |
| `brand.json` | Bowire's configuration: the prefix, the palette role, the family frame, the extra app icons, the brand kit, where the inline copies live and where outputs are copied. |
| `wordmark.json` | "Bowire" in Inter Bold as an outline, so the generator needs no font. |
| `tool/` | The vendored generator with its `pin.json`. Never edited here; `tests/Kuestenlogik.Bowire.Tests/ci/branding-tool.test.mjs` fails a copy that differs from its pin. |

Everything else is generated: `images/` (see `images/README.md`) and its copies on the site, in the docs, in the
workbench (`wwwroot/bowire_mark_{colored,white}.svg`), in the macOS app bundle, and the brand kit
`site/assets/brand/bowire_brand_kit.zip`. So are the inline copies between `brand:` markers in the site's includes,
the docs template, `docs/index.md` and the workbench's `helpers.js`. The brand sheet PDF is rendered from the built
brand page in the site build (`scripts/site/build-brand-pdf.js`).

## Run

```sh
cd scripts/branding/tool && npm ci && cd ../../..   # once per checkout: Playwright and sharp
node scripts/branding/tool/generate-logos.mjs       # regenerate everything after a drawing or brand.json changes
node scripts/branding/tool/generate-logos.mjs --check
node scripts/site/optimize-images.mjs               # the site's AVIF/WebP variants of og-image.png
```

`--check` writes nothing and fails when an output, a copy, the brand kit or an inline copy differs from what the
drawings make. It needs Edge, so it runs on a developer machine before a commit that touches the logo; CI checks the
pin of the tool.

## Decisions (#964, #965)

- **Names** carry the product prefix: `bowire_mark_*`, `bowire_emblem_*`, `bowire_lockup_{h,v}_*`. The web's standard
  names stay: `favicon.ico`, `favicon.svg`, `apple-touch-icon.png`, `icon.png`, `og-image.*`.
- **Colour**: the drawings are black; `colored` paints them in Küstenlogik's navy ink `#003e60`, white on dark.
- **Frame**: the mark is placed in the family frame (77.026421 × 77.673187), scaled whole and centred; the drawing
  itself is unchanged. The emblem keeps its own frame.
- **App icons** (`bowire.ico`, `bowire.icns`, `bowire_app_256.png`, `icon.png`, `icon-512.png`) show the mark on the
  light tile, so a navy mark stays visible on a dark taskbar, dock or gallery.

To change the generator, change the shared tool in the Kuestenlogik repository (`tools/branding`) and vendor it
again (`tool/README.md`, "Vendoring into a product"); never edit `tool/` here.

The wordmark's glyphs are outlines of Inter 4.1 (SIL Open Font License 1.1, `tool/Inter-LICENSE.txt`).
