# Branding tool

One generator for the logo set of every Kuestenlogik product and of the company. The drawings and configurations
live in this repository (`brand/products/<product>/`, `brand/company/`; see `brand/README.md`); a product holds a
pinned copy of the tool and of its own brand folder, and the tool makes every logo file from them, the same set for
every product. The tool names no product.

| File | What it is |
|---|---|
| `generate-logos.mjs` | The generator (Node 22.2 or later, Edge as the browser). |
| `lib/containers.mjs` | ICO, ICNS and ZIP writers. |
| `lib/inline.mjs` | The inline copies between `brand:` markers. |
| `lib/lockup.mjs` | The lockups' geometry and the wordmark's paint per variant. |
| `outline-wordmark.py` | Outlines a wordmark in Inter to `wordmark.json`, so the generator needs no font. |
| `Inter-LICENSE.txt` | The licence of Inter (SIL Open Font License 1.1), whose glyph outlines the wordmarks are. |
| `package.json`, `package-lock.json` | Playwright and sharp, pinned exactly. |
| `check-vendored.mjs` | Checks that a product's copy of this folder and its brand files are exactly what its `pin.json` names. |
| `vendor.mjs` | Copies this folder and the product's brand folder into a product and writes its `pin.json` (stays in this repository). |
| `test/` | Tests of the inline copies and the pin (`node --test tools/branding/test/`; stays in this repository). |

## A product's files

```
scripts/branding/
  brand.json                 the configuration (below)                         vendored
  wordmark.json              the wordmark as an outline (outline-wordmark.py)  vendored
  <prefix>_mark_source.svg   the owner's drawing of the mark                   vendored
  <prefix>_emblem_source.svg optional: a second drawing in its own frame       vendored
  README.md                  optional: the product's own notes                 not vendored
  tool/                      the vendored copy of this folder, with pin.json
images/                      outputs only (and a README.md the generator leaves alone)
```

The brand files are edited in this repository only (`brand/products/<product>/`) and vendored with the tool; the
pin fails a product whose copy differs.

## Run

```sh
cd scripts/branding/tool && npm ci && cd ../../..   # once per checkout: Playwright and sharp
node scripts/branding/tool/generate-logos.mjs       # regenerate images/, the copies and the inline copies
node scripts/branding/tool/generate-logos.mjs --check
node scripts/branding/tool/generate-logos.mjs --contact-sheet artifacts/branding/contact-sheet.png
```

`--config <brand.json>` names a configuration elsewhere (default: `../brand.json`, seen from the tool). `--check`
writes nothing: it renders into a temporary folder and fails when a committed output is missing or not an output,
when a text output (the SVGs) differs from a fresh run, when a copy differs from its committed output, when the brand
kit differs from the committed outputs, or when an inline copy differs from the source. PNGs, WebPs and icons are
checked for presence only, since another Edge version may anti-alias a pixel differently. `--contact-sheet` renders
every SVG and icon at 16 to 512 px on light and on dark into one PNG for review.

`BRANDING_PLAYWRIGHT_FROM` may name another `package.json` to resolve Playwright and sharp from.

## brand.json

Paths are relative to the repository root (`root`, relative to `brand.json`).

| Key | Required | What it sets |
|---|---|---|
| `root` | no (`.`) | The repository root, relative to `brand.json`; a product sets `../..`. |
| `product` | yes | The `aria-label` of every SVG and the lockups' labels. |
| `prefix` | yes | File names (`<prefix>_mark_*`, …) and the adaptive CSS classes (`<prefix>-<role>`). |
| `source` | yes | The drawing of the mark. Read only; Inkscape's editing state stays in it and out of every output. |
| `frame` | no (the drawing's) | `[width, height]` of the family frame. A drawing in another `viewBox` is scaled whole and centred into it; the drawing itself is unchanged. The family's frame is `[77.026421, 77.673187]`. |
| `emblem` | no | `{ "source": "…" }`: a second drawing kept in its own frame (e.g. the mark in a ring). Without it no emblem is made. |
| `wordmark` | yes | `{ "text": "…", "outline": "scripts/branding/wordmark.json" }`; the run stops when the outline holds another text. A wordmark wider than the lockup allows (870 px horizontal, 460 px vertical) is set smaller. |
| `images` | yes | The output folder; the run deletes what it did not write there (`README.md` stays). |
| `roles` | yes | Per role `light` and `dark` colour, `source`: the drawing's fills that mean it, and `why`. Every fill of the drawing must belong to a role, or the run stops. |
| `wordmarkRole` | yes | The role that paints the wordmark (a wordmark in runs paints each run by the run's own role). |
| `background` | yes | `{ "stops": [...] }`: the light background of the tiles and the Open Graph image (a radial gradient). |
| `faviconVariants` | no | Any of `colored`, `black`, `white`: the cropped favicon also in that variant, `favicon_<variant>.svg` and `.ico`, for pages that switch the tab icon by their own theme. |
| `icons` | no | Extra app icons (below). |
| `kit` | no | `{ "path": "…zip" }`: the brand kit (below). |
| `inline` | no | Glob patterns of files that hold inline copies (below). |
| `distribute` | no | `[{ "dir": "…", "files": ["favicon.svg", { "from": "icon.png", "as": "sub/name.png" }] }]`: copies of outputs, under their name or another. |

The fills are read in Edge in the light colour scheme, so the effective fill counts (attribute, style, inherited, the
drawing's own stylesheet, its `prefers-color-scheme: dark` rules left aside). The drawing's stylesheets, classes,
ids and fills are left out of the outputs; each output paints the roles itself.

## Outputs

The same set for every product, in `images/`:

| File | What it is |
|---|---|
| `<prefix>_mark_{colored,black,white,adaptive}.svg` | The mark in the family frame. `adaptive` follows the theme (CSS: `prefers-color-scheme`, and `data-theme` / `data-theme-resolved` where the SVG is inlined). |
| `<prefix>_mark_{colored,black,white}.png`, `@2x.png`, `.webp`, `@2x.webp` | 512 and 1024 px, transparent. |
| `<prefix>_lockup_{h,v}_{colored,black,white,adaptive}.svg` | The mark beside (200 tall) or above (380 tall) the wordmark, as wide as the ink needs (below). |
| `<prefix>_lockup_{h,v}_{colored,black,white}.png`, `@2x.png`, `.webp`, `@2x.webp` | Transparent. |
| `<prefix>_emblem_*` | Only with `emblem`: SVGs as the mark's, PNG and WebP 512 px tall (and @2x). |
| `favicon.svg`, `favicon.ico` | The tab icon: the mark cropped square, adaptive; the ICO 16, 32 and 48 px. |
| `favicon_<variant>.svg`, `.ico` | Only with `faviconVariants`: the same crop, colored, black or white. |
| `apple-touch-icon.png`, `.webp` | 180 px, the mark on the light background, opaque. |
| `icon.png`, `.webp` | 128 px, the same tile: the package icon (NuGet `PackageIcon`, Chocolatey `iconUrl`). |
| `icon-512.png`, `.webp` | 512 px, the same tile: the web manifest icon. |
| `og-image.svg`, `.png`, `.webp` | 1200×630, the horizontal colored lockup on the light background: the link preview. |
| the configured `icons` | ICO, ICNS or PNG (with its WebP). |

Every PNG has a WebP twin, lossless (sharp, libwebp). The `adaptive` SVGs get no PNG: a PNG cannot follow the theme.

A lockup is as wide as its ink needs, the same rule for every product (`lib/lockup.mjs`). Horizontal: 200 tall, a
margin of 10, the mark 180, a gap of 30, the wordmark's ink at 100 px (cap height centred on the mark), a margin of 10;
vertical: 380 tall, the mark 240 at the top, the wordmark's ink at 70 px centred below it, the wider of the two with a
margin of 10 on each side. The width is rounded up to a whole pixel. Since 1.2.0; before, every lockup was 1100 or 480
wide. A wordmark in runs is painted by each run's role in `colored` and `adaptive` only; `black`, `white` and the
inline copies paint every run the one colour.

### App icons (`icons`)

```json
"icons": [
  { "name": "bowire.ico", "sizes": [16, 24, 32, 48, 64, 128, 256] },
  { "name": "bowire.icns", "sizes": [16, 32, 64, 128, 256, 512, 1024], "art": "tile" },
  { "name": "bowire-256.png", "size": 256 }
]
```

`format` is `ico`, `icns` or `png` (default: the name's extension). `art` is `mark` (the mark cropped square,
colored, transparent; the default) or `tile` (the mark on the light background, opaque). ICNS holds 16, 32, 64, 128,
256, 512 and 1024 px. Copy them where they are used with `distribute` (a csproj's `ApplicationIcon`, an MSI, a macOS
`.app`, a Linux `hicolor/256x256/apps/<name>.png`, a VS Code extension's `icon.png`).

### Brand kit (`kit`)

A ZIP of every output but the README, entries `<prefix>/<file>` in name order, stored, dated 1980-01-01: the same
outputs give the same bytes. `--check` compares it byte for byte with a kit made from the committed outputs.

### Inline copies (`inline`)

A page or script that inlines the logo keeps the copy between markers, and the generator rewrites what lies between
them from the drawing:

```html
<!-- brand:mark class="logo" aria-hidden="true" --><!-- /brand -->
```

```js
const logo = /* brand:lockup-h class="logo" */ '' /* /brand */;
```

In a script the markers stand outside the string literal, as comments of the code; the generator writes a
single-quoted string between them (`'` and `\` escaped). A marker inside a literal would be part of the string and
show on the page; since 1.1.0 a marker left inside a literal breaks the script, so move it out.

The kinds are `mark`, `emblem`, `lockup-h` and `lockup-v`. The marker's attributes go onto the `<svg>`; without
`aria-hidden`, `aria-label` or `role` it gets `role="img"` and `aria-label` = the product. Every shape is painted
`currentColor`, and the copy is one line. `inline` lists glob patterns (`"site/**/*.html"`); files without a marker
are left alone, but at least one must hold one.

## Vendoring into a product

A product holds a copy of this folder in `scripts/branding/tool/` and a copy of its brand folder
(`brand/products/<product>/`: the drawings, `brand.json`, `wordmark.json`) in `scripts/branding/`, so a fresh clone
generates and checks its logos without this repository beside it. Both are pinned: `pin.json` names the version
(`package.json`), the commit of this repository they came from, the SHA-256 of every file of the tool, and under
`brand` the folder they came from and the SHA-256 of each brand file (LF line endings):

```json
{
  "version": "1.2.0",
  "commit": "<40 hex>",
  "files": { "generate-logos.mjs": "<sha256>", "…": "…" },
  "brand": {
    "from": "brand/products/terracle",
    "files": { "brand.json": "<sha256>", "terracle_mark_source.svg": "<sha256>", "wordmark.json": "<sha256>" }
  }
}
```

A test in each product runs the check (`check-vendored.mjs`, or the same hashes taken on their own), which fails when
a file of the tool or a brand file differs from the pin, is missing or is not part of it (in `scripts/branding/` only
`tool/` and a `README.md` are the product's own). So neither the tool nor a drawing is edited in a product.

To change a drawing, a configuration or the tool:

1. edit it here (`brand/products/<product>/`; or `tools/branding/`, raising `version` in `package.json`, and
   `npm install` when a dependency changes), commit and push;
2. from a clean checkout of this repository: `node tools/branding/vendor.mjs <product> <product checkout>` (writes
   `scripts/branding/tool/`, the brand files in `scripts/branding/` and `pin.json`);
3. in the product: `cd scripts/branding/tool && npm ci && cd ../../..`, `node scripts/branding/tool/generate-logos.mjs`,
   then `node scripts/branding/tool/generate-logos.mjs --check`;
4. commit the copy, the brand files, `pin.json` and the regenerated outputs together in the product.

`node scripts/branding/tool/check-vendored.mjs --against <this repository>/tools/branding` says whether a product's
copy is behind this repository (the tool and the brand folder).

To take a new product on: create `brand/products/<product>/` with its drawing, `brand.json` (paths as seen from the
product's repository, `root` `../..`) and its wordmark
(`outline-wordmark.py <Inter-Bold.ttf> --text <Product> --out brand/products/<product>/wordmark.json`), commit,
vendor it, add the check to the product's tests, run the generator there.

## The wordmark

`wordmark.json` is committed, so the generator needs no font. Make it again only when the text or the font changes
(in `brand/products/<product>/` or `brand/company/`, with `--out`):

```sh
python -m venv .venv && .venv/Scripts/python -m pip install fonttools uharfbuzz
.venv/Scripts/python tools/branding/outline-wordmark.py <path/to/Inter-Bold.ttf> --text <Product> --out <wordmark.json>
```

A wordmark set in more than one weight or colour (the company's lettering: its first word regular in KL Blue, the
second bold in Blue Deep, tracked; see `brand/README.md`) is outlined in runs, each with its font and the role of
`brand.json` that paints it:

```sh
.venv/Scripts/python tools/branding/outline-wordmark.py --run <FIRST> Inter-Regular.ttf accent \
  --run <SECOND> Inter-Bold.ttf structure --tracking 0.0714286 --gap 0.0714286 --out brand/company/wordmark.json
```

`--tracking` is the letter spacing after every glyph in em (4 at 56 px), `--gap` extra space between two runs in em.
Its `wordmark.json` holds `parts` (one path per run with its `role`) instead of `d`.

The font is Inter 4.1 from its official release (<https://github.com/rsms/inter/releases>, `Inter-4.1.zip`,
`extras/ttf/Inter-Bold.ttf` and `Inter-Regular.ttf`), SIL Open Font License 1.1, licence text in `Inter-LICENSE.txt`. HarfBuzz shapes the text,
so the font's kerning applies.

## Third-party

Only the generator uses these; no product ships them: Playwright 1.64.1 (Apache-2.0) drives Edge; sharp 0.35.5
(Apache-2.0) with its prebuilt libvips (LGPL-3.0-or-later) encodes the WebPs.
