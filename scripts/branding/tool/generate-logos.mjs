// A product's logo set, generated from the owner's drawing of its mark. See README.md beside this file.
//
//   node generate-logos.mjs [--config <brand.json>] [--check] [--contact-sheet <file.png>]
//
// Everything product-specific comes from the configuration (default: brand.json in the folder above this tool, where
// a product keeps it beside its vendored copy of the tool): the product name, the output prefix, the source drawing
// and the frame it is placed in, an optional emblem, the wordmark (its text and its outline), the colour roles, the
// light background, extra app icons, the brand kit, the inline copies and where outputs are copied to. The script
// names no product.
//
// Sources (the only ones; everything in the images folder is derived and may be deleted and regenerated):
//   source             the mark, the owner's drawing; read only, never rewritten
//   emblem.source      optional: a second drawing in its own frame (e.g. the mark in a ring); read only
//   wordmark.outline   the wordmark text as an outline (outline-wordmark.py), so no font is needed; a wordmark in
//                      runs (its "parts") paints each run by the run's role, otherwise wordmarkRole paints it
//
// Steps:
//   1. read the source in Edge (light colour scheme); every shape's fill is mapped to a role; an unknown fill stops
//      the run; a source drawn in another frame is scaled whole and centred into the configured one;
//   2. write the mark, emblem and lockup SVGs in colored, black, white and adaptive, cleaned: no Inkscape/sodipodi
//      metadata, no styles or classes of the drawing, no unused defs or empty groups, role="img" and aria-label;
//      favicon.svg (the mark, cropped square, adaptive) and, with faviconVariants, favicon_<variant>.svg/.ico;
//      og-image.svg (1200×630);
//   3. render PNGs in Edge with a transparent background (1x and @2x), each also as a lossless WebP; favicon.ico
//      (16/32/48), apple-touch-icon.png (180), icon.png (128, the package icon) and icon-512.png (the web manifest
//      icon), the last three the mark on the light background; og-image.png; the configured app icons (ICO, ICNS,
//      PNG); the brand kit ZIP;
//   4. delete every file in the images folder the run did not write (README.md stays);
//   5. copy outputs to the places the configuration names, and rewrite the marked inline copies.
//
// --check writes nothing: it renders into a temporary folder and fails when the committed outputs, their copies, the
// brand kit or the inline copies differ from what a run would write (text files by content, binaries by presence;
// the copies and the kit byte for byte against the committed outputs).
//
// Playwright and sharp come from this folder (npm ci here); BRANDING_PLAYWRIGHT_FROM may name another package.json
// to resolve them from. Edge is the browser (channel msedge).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { ico, icns, icnsSizes, zip } from './lib/containers.mjs';
import { syncInline, hasMarker } from './lib/inline.mjs';
import { flat, lockupGeometry, partPaint } from './lib/lockup.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const arg = (name) => (args.includes(name) ? path.resolve(args[args.indexOf(name) + 1]) : null);
const configFile = arg('--config') ?? path.join(here, '..', 'brand.json');
const contactSheet = arg('--contact-sheet');
const check = args.includes('--check');
if (!fs.existsSync(configFile)) throw new Error(`${configFile} does not exist: name the product's brand.json with --config`);

const config = JSON.parse(fs.readFileSync(configFile, 'utf8'));
for (const key of ['product', 'prefix', 'source', 'wordmark', 'images', 'roles', 'wordmarkRole', 'background']) {
  if (config[key] === undefined) throw new Error(`${configFile}: "${key}" is missing`);
}
const root = path.resolve(path.dirname(configFile), config.root ?? '.');
const images = path.join(root, config.images);
const source = path.join(root, config.source);
const emblemSource = config.emblem?.source ? path.join(root, config.emblem.source) : null;
const label = config.product;
const prefix = config.prefix;
const toolRequire = createRequire(process.env.BRANDING_PLAYWRIGHT_FROM ?? path.join(here, 'package.json'));
const { chromium } = toolRequire('playwright');
const sharp = toolRequire('sharp');

// Palette. A role per colour of the drawing (source: the fills that mean it); the adaptive files swap each role's
// light colour for its dark one.
const roles = Object.fromEntries(Object.entries(config.roles).map(([role, c]) =>
  [role, { light: c.light, dark: c.dark, source: c.source.map((v) => v.toLowerCase()) }]));
if (!roles[config.wordmarkRole]) throw new Error(`${configFile}: wordmarkRole "${config.wordmarkRole}" is no role`);
// The light background behind the tiles (touch, package and manifest icon) and the Open Graph image: a radial
// gradient over these stops.
const glass = config.background.stops;

const wordmark = JSON.parse(fs.readFileSync(path.join(root, config.wordmark.outline), 'utf8'));
if (wordmark.text !== config.wordmark.text) {
  throw new Error(`${config.wordmark.outline} outlines "${wordmark.text}", the configuration names "${config.wordmark.text}": run outline-wordmark.py --text`);
}
// A wordmark in runs (outline-wordmark.py --run) paints each run by its own role; otherwise wordmarkRole paints it.
const wordmarkParts = wordmark.parts ?? [{ role: config.wordmarkRole, d: wordmark.d }];
for (const part of wordmarkParts) {
  if (!roles[part.role]) throw new Error(`${config.wordmark.outline}: the run "${part.text}" names role "${part.role}", which brand.json does not have`);
}

// Lockup geometry of the family (lib/lockup.mjs): 200 tall beside, 380 tall above, each as wide as mark, gap and the
// wordmark's ink need with the margin.
const lockups = {
  h: { ...lockupGeometry('h', wordmark), label: `${label} logo horizontal` },
  v: { ...lockupGeometry('v', wordmark), label: `${label} logo vertical` },
};
const markPng = 512;
const emblemPngHeight = 512;
// The Open Graph image: 1200×630, the horizontal lockup's ink 840 px wide (at most 240 tall) in its middle, so it
// reads in a link preview's thumbnail.
const og = { w: 1200, h: 630, inkWidth: 840, inkHeight: 240 };
// The tiles: the mark on the light glass, square and opaque, at these sizes.
const tiles = [
  { name: 'apple-touch-icon', size: 180 },
  { name: 'icon', size: 128 },
  { name: 'icon-512', size: 512 },
];
const variants = ['colored', 'black', 'white', 'adaptive'];

const target = check ? fs.mkdtempSync(path.join(os.tmpdir(), 'branding-check-')) : images;
const written = new Set(['README.md']);
const out = (name) => { written.add(name); return path.join(target, name); };
const shown = (file) => (check ? path.join(config.images, path.relative(target, file)) : path.relative(root, file));
const write = (file, text) => { fs.writeFileSync(file, text.replace(/\r\n/g, '\n')); if (!check) console.log('  wrote', shown(file)); };
const writeBinary = (file, data) => { fs.writeFileSync(file, data); if (!check) console.log('  wrote', shown(file)); };
// A PNG output and its lossless WebP twin.
async function writePng(name, png) {
  writeBinary(out(`${name}.png`), png);
  writeBinary(out(`${name}.webp`), await sharp(png).webp({ lossless: true, effort: 6 }).toBuffer());
}

// 1. Inkscape's editing state (the namedview, inkscape:/sodipodi: attributes and their namespaces) stays in the
//    source and is left out of every output. Paths, transforms and styles are taken as drawn.
const editorless = (text) => text
  .replace(/\s*<sodipodi:namedview[\s\S]*?<\/sodipodi:namedview>/g, '')
  .replace(/\s*<sodipodi:namedview[^>]*\/>/g, '')
  .replace(/\s+(?:inkscape|sodipodi):[\w-]+="[^"]*"/g, '')
  .replace(/\s+xmlns:(?:inkscape|sodipodi|svg)="[^"]*"/g, '');

// The drawing's body with a role class on every filled shape, read in the browser in the light colour scheme, so the
// effective fill (attribute, style, the drawing's own stylesheet, inherited from a group) is what counts.
async function readBody(page, svgText, file) {
  await page.setContent(`<!doctype html><body style="margin:0">${editorless(svgText).replace(/<\?xml[^>]*>/, '')}</body>`);
  const body = await page.evaluate((roleSources) => {
    const svg = document.querySelector('svg');
    const hex = (c) => {
      const m = c.match(/\d+(\.\d+)?/g);
      if (!m || c === 'none') return c;
      return '#' + m.slice(0, 3).map((v) => (+v).toString(16).padStart(2, '0')).join('');
    };
    const unknown = [];
    for (const el of svg.querySelectorAll('path, circle, ellipse, rect, polygon, polyline, line')) {
      if (el.closest('defs, clipPath, mask, symbol')) continue;
      const fill = hex(getComputedStyle(el).fill);
      const role = Object.entries(roleSources).find(([, s]) => s.includes(fill))?.[0];
      if (!role) { unknown.push(`${el.id || el.tagName}: ${fill}`); continue; }
      el.setAttribute('data-role', role);
    }
    if (unknown.length) return { error: 'fills without a role: ' + unknown.join(', ') };
    const bb = svg.getBBox();
    // The drawing's own stylesheets and classes are read now and would fight the outputs' paint: they go. Fills go
    // too (they come back per variant), ids (nothing refers to them; inlined twice they would clash), and style
    // properties that only matter to the fill. Inkscape leaves empty groups behind; they draw nothing.
    for (const s of [...svg.querySelectorAll('style')]) s.remove();
    for (const g of [...svg.querySelectorAll('g')].reverse()) if (!g.closest('defs') && !g.children.length) g.remove();
    for (const el of svg.querySelectorAll('*')) {
      if (el.closest('defs')) continue;
      el.removeAttribute('fill');
      el.removeAttribute('id');
      el.removeAttribute('class');
      const style = el.getAttribute('style');
      if (style !== null) {
        const kept = style.split(';').map((s) => s.trim()).filter((s) => s && !/^fill(-opacity)?\s*:/.test(s));
        if (kept.length) el.setAttribute('style', kept.join(';')); else el.removeAttribute('style');
      }
    }
    const parts = [...svg.children].filter((c) => c.tagName !== 'defs').map((c) => new XMLSerializer().serializeToString(c));
    return { body: parts.join('\n').replace(/ xmlns="http:\/\/www\.w3\.org\/2000\/svg"/g, ''), viewBox: svg.getAttribute('viewBox'), bbox: [bb.x, bb.y, bb.width, bb.height] };
  }, Object.fromEntries(Object.entries(roles).map(([k, v]) => [k, v.source])));
  if (body.error) throw new Error(`${path.basename(file)}: ${body.error}`);
  if (!body.viewBox) throw new Error(`${path.basename(file)}: the drawing has no viewBox`);
  return body;
}

// A mark drawn in another frame than the family's is scaled whole and centred into it; the drawing is unchanged.
function placeInFrame(drawing, frame) {
  const [vx, vy, vw, vh] = drawing.viewBox.trim().split(/[\s,]+/).map(Number);
  const [fw, fh] = frame;
  if (vx === 0 && vy === 0 && Math.abs(vw - fw) < 1e-6 && Math.abs(vh - fh) < 1e-6) return drawing;
  const s = Math.min(fw / vw, fh / vh);
  const tx = (fw - vw * s) / 2 - vx * s, ty = (fh - vh * s) / 2 - vy * s;
  const f = (v) => +v.toFixed(6);
  const [bx, by, bw, bh] = drawing.bbox;
  return {
    body: `<g transform="translate(${f(tx)} ${f(ty)}) scale(${f(s)})">\n${drawing.body}\n</g>`,
    viewBox: `0 0 ${fw} ${fh}`,
    bbox: [bx * s + tx, by * s + ty, bw * s, bh * s],
  };
}

// 2. Variants. `current` paints every shape currentColor (the inline copies).
const paint = (body, variant) => body.replace(/data-role="(\w+)"/g, (_, role) =>
  variant === 'adaptive' ? `class="${prefix}-${role}"` : `fill="${flat[variant] ?? roles[role].light}"`);

// Theme-aware CSS: the system's scheme, and the page's own toggle where the SVG is inlined
// (data-theme-resolved as some products' pages set it, data-theme as others do).
function adaptiveStyle() {
  const rule = (scheme, scope = '') => Object.entries(roles)
    .map(([role, c]) => `${scope}.${prefix}-${role} { fill: ${c[scheme]}; }`).join('\n    ');
  const toggled = (scheme) => Object.entries(roles).map(([role, c]) =>
    `:root[data-theme-resolved="${scheme}"] .${prefix}-${role}, [data-theme-resolved="${scheme}"] .${prefix}-${role},\n    :root[data-theme="${scheme}"] .${prefix}-${role}, [data-theme="${scheme}"] .${prefix}-${role} { fill: ${c[scheme]}; }`).join('\n    ');
  return `<style>
    ${rule('light')}
    @media (prefers-color-scheme: dark) {
      ${rule('dark').replace(/\n {4}/g, '\n      ')}
    }
    /* The page's own theme toggle; only effective when the SVG is inlined. */
    ${toggled('dark')}
    ${toggled('light')}
  </style>`;
}

const svgDoc = (viewBox, label, inner, style = '') =>
  `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}" role="img" aria-label="${label}">
${style ? `  <defs>${style}</defs>\n` : ''}${inner}
</svg>
`;

// A lockup's inner drawing (the mark beside or above the wordmark) in a variant.
function lockupInner(kind, variant, mark) {
  const L = lockups[kind];
  const { s, tx, baseline } = L.text;
  const text = wordmarkParts.map((part) =>
    `  <path ${partPaint(variant, part.role, roles, prefix)} transform="translate(${tx.toFixed(2)} ${baseline.toFixed(2)}) scale(${s.toFixed(6)})" d="${part.d}" />`).join('\n');
  return `  <svg x="${+L.mark.x.toFixed(3)}" y="${+L.mark.y.toFixed(3)}" width="${L.mark.size}" height="${L.mark.size}" viewBox="${square(mark, 1).map((v) => +v.toFixed(3)).join(' ')}">
${paint(mark.body, variant)}
  </svg>
${text}`;
}

const lockup = (kind, variant, mark) =>
  svgDoc(`0 0 ${lockups[kind].w} ${lockups[kind].h}`, lockups[kind].label, lockupInner(kind, variant, mark), variant === 'adaptive' ? adaptiveStyle() : '');

// A square around the drawing's ink, `air` times its larger side.
function square(mark, air) {
  const [x, y, w, h] = mark.bbox;
  const side = Math.max(w, h) * air;
  return [x + w / 2 - side / 2, y + h / 2 - side / 2, side, side];
}

// The favicon: the mark in a square around its drawing, a little air on every side.
function favicon(mark, variant = 'adaptive') {
  const vb = square(mark, 1.06).map((v) => +v.toFixed(3)).join(' ');
  return svgDoc(vb, label, paint(mark.body, variant), variant === 'adaptive' ? adaptiveStyle() : '');
}

// A tile: the mark on the light glass, square, no transparency (iOS fills a transparent touch icon black, and a
// package gallery's background is unknown).
function tile(mark) {
  const [ox, oy, side] = square(mark, 1.4);
  return svgDoc(`${ox.toFixed(3)} ${oy.toFixed(3)} ${side.toFixed(3)} ${side.toFixed(3)}`, label, `  <rect x="${ox.toFixed(3)}" y="${oy.toFixed(3)}" width="${side.toFixed(3)}" height="${side.toFixed(3)}" fill="url(#glass)" />
${paint(mark.body, 'colored')}`, glassGradient());
}

function glassGradient() {
  const offsets = glass.map((_, i) => (i === 0 ? 0 : i === glass.length - 1 ? 1 : +(0.7 * i / (glass.length - 2)).toFixed(3)));
  return `<radialGradient id="glass" cx="0.3" cy="0.25" r="0.95">${glass.map((c, i) => `<stop offset="${offsets[i]}" stop-color="${c}" />`).join('')}</radialGradient>`;
}

// The Open Graph image: the horizontal colored lockup centred on the light background, opaque.
function ogImage(lockupH, ink) {
  const [ix, iy, iw, ih] = ink;
  const k = Math.min(og.inkWidth / iw, og.inkHeight / ih);
  const w = iw * k, h = ih * k;
  const f = (v) => +v.toFixed(3);
  const inner = lockupH.replace(/<\?xml[^>]*>\s*/, '')
    .replace(/<svg [^>]*>/, `<svg x="${f((og.w - w) / 2)}" y="${f((og.h - h) / 2)}" width="${f(w)}" height="${f(h)}" viewBox="${[ix, iy, iw, ih].map(f).join(' ')}">`)
    .trimEnd();
  return svgDoc(`0 0 ${og.w} ${og.h}`, label, `  <rect width="${og.w}" height="${og.h}" fill="url(#glass)" />
${inner}`, glassGradient());
}

// The ink of a drawing (its bounding box in its own units), measured in the browser.
async function inkOf(page, svgText) {
  await page.setContent(`<!doctype html><body style="margin:0">${svgText.replace(/<\?xml[^>]*>/, '')}</body>`);
  return page.evaluate(() => { const b = document.querySelector('svg').getBBox(); return [b.x, b.y, b.width, b.height]; });
}

// 3. Rendering. The SVG fills a viewport of the target's CSS size; @2x doubles the device pixels.
async function render(browser, svgText, w, h, scale = 1, colorScheme = 'light') {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: scale, colorScheme });
  const page = await ctx.newPage();
  const sized = svgText.replace(/<\?xml[^>]*>/, '').replace('<svg ', `<svg width="${w}" height="${h}" style="display:block" `);
  await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent">${sized}</body></html>`);
  const png = await page.screenshot({ omitBackground: true });
  await ctx.close();
  return png;
}

// A copy target: "name" or { "from": "name", "as": "other/name.png" }.
const copyOf = (entry) => (typeof entry === 'string' ? { from: entry, as: entry } : { from: entry.from, as: entry.as ?? entry.from });
const isText = (file) => /\.(svg|json|html?|txt|css|js|mjs|md|xml)$/i.test(file);
const sameText = (a, b) => a.replace(/\r\n/g, '\n') === b.replace(/\r\n/g, '\n');

// ---------------------------------------------------------------------------------------------------------------
const browser = await chromium.launch({ channel: 'msedge' });
const drift = [];
try {
  const context = await browser.newContext({ colorScheme: 'light' });
  const page = await context.newPage();
  console.log('==> source', config.source);
  let mark = await readBody(page, fs.readFileSync(source, 'utf8'), source);
  if (config.frame) mark = placeInFrame(mark, config.frame);
  const emblem = emblemSource ? await readBody(page, fs.readFileSync(emblemSource, 'utf8'), emblemSource) : null;

  const pngJobs = [];
  console.log('==> mark');
  for (const variant of variants) {
    const text = svgDoc(mark.viewBox, label, paint(mark.body, variant), variant === 'adaptive' ? adaptiveStyle() : '');
    write(out(`${prefix}_mark_${variant}.svg`), text);
    if (variant !== 'adaptive') pngJobs.push({ svg: text, name: `${prefix}_mark_${variant}`, w: markPng, h: markPng });
  }

  if (emblem) {
    console.log('==> emblem', config.emblem.source);
    const [, , ew, eh] = emblem.viewBox.trim().split(/[\s,]+/).map(Number);
    for (const variant of variants) {
      const text = svgDoc(emblem.viewBox, `${label} emblem`, paint(emblem.body, variant), variant === 'adaptive' ? adaptiveStyle() : '');
      write(out(`${prefix}_emblem_${variant}.svg`), text);
      if (variant !== 'adaptive') pngJobs.push({ svg: text, name: `${prefix}_emblem_${variant}`, w: Math.round(emblemPngHeight * ew / eh), h: emblemPngHeight });
    }
  }

  console.log('==> lockups');
  let lockupH = null;
  for (const kind of ['h', 'v']) {
    for (const variant of variants) {
      const text = lockup(kind, variant, mark);
      write(out(`${prefix}_lockup_${kind}_${variant}.svg`), text);
      if (kind === 'h' && variant === 'colored') lockupH = text;
      if (variant !== 'adaptive') pngJobs.push({ svg: text, name: `${prefix}_lockup_${kind}_${variant}`, w: lockups[kind].w, h: lockups[kind].h });
    }
  }

  console.log('==> PNGs and WebPs');
  for (const j of pngJobs) {
    await writePng(j.name, await render(browser, j.svg, j.w, j.h, 1));
    await writePng(`${j.name}@2x`, await render(browser, j.svg, j.w, j.h, 2));
  }

  console.log('==> favicon and tiles');
  const fav = favicon(mark);
  write(out('favicon.svg'), fav);
  const icoPngs = [];
  for (const size of [16, 32, 48]) icoPngs.push({ size, data: await render(browser, fav, size, size) });
  writeBinary(out('favicon.ico'), ico(icoPngs));
  // Optional: the cropped favicon in fixed variants, for pages that switch the tab icon by their own theme.
  for (const variant of config.faviconVariants ?? []) {
    if (variant === 'adaptive' || !variants.includes(variant)) throw new Error(`faviconVariants: "${variant}" is none of colored, black, white`);
    const text = favicon(mark, variant);
    write(out(`favicon_${variant}.svg`), text);
    const pngs = [];
    for (const size of [16, 32, 48]) pngs.push({ size, data: await render(browser, text, size, size) });
    writeBinary(out(`favicon_${variant}.ico`), ico(pngs));
  }
  const tileSvg = tile(mark);
  for (const t of tiles) await writePng(t.name, await render(browser, tileSvg, t.size, t.size));

  if (config.icons?.length) {
    console.log('==> app icons');
    const art = { mark: favicon(mark, 'colored'), tile: tileSvg };
    for (const icon of config.icons) {
      const svg = art[icon.art ?? 'mark'];
      if (!svg) throw new Error(`icons: ${icon.name}: art "${icon.art}" is neither mark nor tile`);
      const sizes = icon.sizes ?? (icon.size ? [icon.size] : null);
      if (!sizes?.length) throw new Error(`icons: ${icon.name}: no sizes`);
      const pngs = [];
      for (const size of sizes) pngs.push({ size, data: await render(browser, svg, size, size) });
      const format = icon.format ?? path.extname(icon.name).slice(1);
      if (format === 'ico') writeBinary(out(icon.name), ico(pngs));
      else if (format === 'icns') {
        const wrong = sizes.filter((s) => !icnsSizes.includes(s));
        if (wrong.length) throw new Error(`icons: ${icon.name}: ICNS holds ${icnsSizes.join(', ')} px, not ${wrong.join(', ')}`);
        writeBinary(out(icon.name), icns(pngs));
      } else if (format === 'png') {
        if (pngs.length !== 1 || !icon.name.endsWith('.png')) throw new Error(`icons: ${icon.name}: a PNG icon has one size and ends in .png`);
        await writePng(icon.name.slice(0, -4), pngs[0].data);
      } else throw new Error(`icons: ${icon.name}: format "${format}" is none of ico, icns, png`);
    }
  }

  console.log('==> Open Graph image');
  const ogSvg = ogImage(lockupH, await inkOf(page, lockupH));
  write(out('og-image.svg'), ogSvg);
  await writePng('og-image', await render(browser, ogSvg, og.w, og.h));
  await context.close();

  // The brand kit: every output but the README in one ZIP, from the files as committed (in --check the committed
  // ones, so the kit is compared with what the repository holds).
  const outputs = [...written].filter((f) => f !== 'README.md').sort();
  if (config.kit?.path) {
    const kitFile = path.join(root, config.kit.path);
    const inImages = path.dirname(kitFile) === images;
    const members = outputs.filter((f) => !(inImages && f === path.basename(kitFile)));
    if (inImages) written.add(path.basename(kitFile));
    const from = check ? images : target;
    const missing = members.filter((f) => !fs.existsSync(path.join(from, f)));
    const data = missing.length ? null : zip(members.map((f) => ({ name: `${prefix}/${f}`, data: fs.readFileSync(path.join(from, f)) })));
    if (!check) { console.log('==> brand kit'); fs.mkdirSync(path.dirname(kitFile), { recursive: true }); writeBinary(kitFile, data); }
    else if (!data || !fs.existsSync(kitFile) || !fs.readFileSync(kitFile).equals(data)) drift.push(`${config.kit.path}: the brand kit differs from the committed outputs`);
  }

  if (check) {
    console.log('==> compare', config.images);
    for (const f of [...written].filter((f) => f !== 'README.md')) {
      const committed = path.join(images, f);
      if (!fs.existsSync(committed)) { drift.push(`${config.images}/${f}: missing`); continue; }
      const fresh = path.join(target, f);
      if (isText(f) && fs.existsSync(fresh) && !sameText(fs.readFileSync(committed, 'utf8'), fs.readFileSync(fresh, 'utf8'))) {
        drift.push(`${config.images}/${f}: differs from a fresh run`);
      }
    }
    for (const f of fs.existsSync(images) ? fs.readdirSync(images) : []) {
      if (!written.has(f)) drift.push(`${config.images}/${f}: no output of the generator`);
    }
  } else {
    console.log('==> clean', config.images);
    for (const f of fs.readdirSync(images)) {
      if (written.has(f)) continue;
      fs.rmSync(path.join(images, f), { recursive: true });
      console.log('  deleted', path.join(config.images, f));
    }
  }

  console.log(check ? '==> compare copies' : '==> distribute');
  for (const { dir, files } of config.distribute ?? []) {
    for (const entry of files) {
      const { from, as } = copyOf(entry);
      if (!written.has(from) || from === 'README.md') throw new Error(`distribute: ${from} is no output of this run`);
      const copy = path.join(root, dir, as);
      const shownCopy = path.posix.join(dir, as.replace(/\\/g, '/'));
      if (check) {
        if (!fs.existsSync(copy)) { drift.push(`${shownCopy}: missing (a copy of ${from})`); continue; }
        const a = fs.readFileSync(copy), b = fs.readFileSync(path.join(images, from));
        const same = isText(from) ? sameText(a.toString('utf8'), b.toString('utf8')) : a.equals(b);
        if (!same) drift.push(`${shownCopy}: differs from ${config.images}/${from}`);
      } else {
        fs.mkdirSync(path.dirname(copy), { recursive: true });
        fs.copyFileSync(path.join(images, from), copy);
        console.log('  copied', shownCopy);
      }
    }
  }

  if (config.inline?.length) {
    console.log(check ? '==> compare inline copies' : '==> inline copies');
    const drawings = {
      mark: { viewBox: mark.viewBox, body: paint(mark.body, 'current') },
      emblem: emblem ? { viewBox: emblem.viewBox, body: paint(emblem.body, 'current') } : null,
      'lockup-h': { viewBox: `0 0 ${lockups.h.w} ${lockups.h.h}`, body: lockupInner('h', 'current', mark) },
      'lockup-v': { viewBox: `0 0 ${lockups.v.w} ${lockups.v.h}`, body: lockupInner('v', 'current', mark) },
    };
    const files = new Set();
    for (const pattern of config.inline) {
      for (const f of fs.globSync(pattern, { cwd: root, exclude: (p) => /(^|[\\/])node_modules$/.test(p) })) files.add(f);
    }
    let marked = 0;
    for (const f of [...files].sort()) {
      const file = path.join(root, f);
      if (!fs.statSync(file).isFile()) continue;
      const text = fs.readFileSync(file, 'utf8');
      if (!hasMarker(text)) continue;
      marked++;
      const next = syncInline(text, f, label, (kind) => drawings[kind]);
      if (next === text) continue;
      if (check) drift.push(`${f.replace(/\\/g, '/')}: an inline copy differs from the source`);
      else { fs.writeFileSync(file, next); console.log('  rewrote', f.replace(/\\/g, '/')); }
    }
    if (!marked) throw new Error(`inline: no file matching ${config.inline.join(', ')} holds a brand: marker`);
  }

  if (contactSheet && !check) {
    console.log('==> contact sheet');
    // Every output at 16, 32, 64, 180 and 512 px tall (the wide ones up to 180: at 512 the horizontal lockup is
    // 2816 px wide), on white and on #121212: the SVGs, favicon.ico and the tiles (each other PNG is a render of the
    // SVG of its name and shows the same picture).
    const files = fs.readdirSync(images).filter((f) => /\.svg$/.test(f) || ['favicon.ico', ...tiles.map((t) => `${t.name}.png`), 'og-image.png'].includes(f)).sort();
    const sizes = [16, 32, 64, 180, 512];
    const row = (f, dark) => {
      const wide = f.includes('lockup_h') || f.includes('_emblem_') || f.startsWith('og-image');
      const imgs = sizes.filter((s) => !(wide && s > 180)).map((s) => `<img src="${f}" style="height:${s}px">`).join('');
      return `<div class="row" style="background:${dark ? '#121212' : '#ffffff'}"><code style="color:${dark ? '#ddd' : '#333'}">${f}</code>${imgs}</div>`;
    };
    const head = `<!doctype html><html><head><meta charset="utf-8"><style>
      body { margin: 24px; font: 13px Segoe UI, sans-serif; background: #8a96a3; }
      h2 { margin: 24px 0 8px; color: #fff; }
      .row { display: flex; align-items: flex-end; gap: 16px; padding: 10px 12px; margin-bottom: 6px; border-radius: 6px; width: max-content; }
      .row code { width: 270px; flex: none; align-self: center; font-family: Consolas, monospace; font-size: 12px; }
    </style></head><body>`;
    // An SVG in <img> follows the embedding page's colour scheme: the light half is rendered in light mode, the
    // dark half in dark mode, so the adaptive files show both faces; the halves stand side by side in one image.
    const halves = [];
    for (const dark of [false, true]) {
      const ctx = await browser.newContext({ viewport: { width: 1600, height: 400 }, colorScheme: dark ? 'dark' : 'light' });
      const p = await ctx.newPage();
      const title = dark ? 'Dark background (#121212), the page in dark mode' : 'Light background (#ffffff)';
      const part = head + `<h2>${title}: 16, 32, 64, 180, 512 px</h2>${files.map((f) => row(f, dark)).join('')}</body>`;
      const tmp = path.join(images, `__contact-${dark ? 'dark' : 'light'}.html`);
      fs.writeFileSync(tmp, part);
      await p.goto('file:///' + tmp.replace(/\\/g, '/'));
      await p.waitForLoadState('networkidle');
      halves.push({ file: tmp, png: await p.screenshot({ fullPage: true }) });
      await ctx.close();
    }
    const ctx = await browser.newContext({ viewport: { width: halves.reduce((w, h) => w + h.png.readUInt32BE(16), 0), height: 400 } });
    const p = await ctx.newPage();
    await p.setContent(`<body style="margin:0;background:#8a96a3;white-space:nowrap;font-size:0">${halves.map((h) => `<img style="display:inline-block;vertical-align:top" src="data:image/png;base64,${h.png.toString('base64')}">`).join('')}</body>`);
    await p.waitForLoadState('load');
    fs.mkdirSync(path.dirname(contactSheet), { recursive: true });
    fs.writeFileSync(contactSheet, await p.screenshot({ fullPage: true }));
    await ctx.close();
    for (const h of halves) fs.rmSync(h.file);
    console.log('  wrote', contactSheet);
  }
} finally {
  await browser.close();
  if (check) fs.rmSync(target, { recursive: true, force: true });
}

if (check) {
  if (drift.length) {
    console.error(`\n${drift.length} difference(s); run the generator and commit its outputs:`);
    for (const d of drift) console.error('  ' + d);
    process.exit(1);
  }
  console.log('\nThe outputs, their copies and the inline copies match the sources.');
}
