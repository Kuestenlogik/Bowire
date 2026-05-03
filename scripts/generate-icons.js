#!/usr/bin/env node
/**
 * Bowire icon generator.
 *
 * Regenerates every derivative icon artefact (PNG / WebP / ICO) from
 * the two source SVGs under images/. This script only writes files
 * inside images/ — to push the generated assets into the embedded UI /
 * docs / marketing site, run the sibling `distribute-icons.js`
 * afterwards (or the combined wrapper `generate-icons.sh` +
 * `distribute-icons.sh` one after the other).
 *
 * Sources (author-owned, committed):
 *   images/bowire_logo.svg        — large horizontal logo (hero, docs)
 *   images/bowire_logo_small.svg  — square 1:1 logo (favicon, app icon)
 *
 * Both source SVGs are theme-aware: they carry an embedded
 * <style>@media (prefers-color-scheme: dark){…}</style> block so a
 * single file flips between black-on-light and white-on-dark in any
 * context that respects OS theme (browser favicon, README embed, GitHub
 * blob viewer). The Bowire workbench has its own theme switcher that
 * needs to override OS preference; BowireHtmlGenerator strips the
 * media-query block at runtime when colour must be locked.
 *
 * Idempotent: delete every derivative in images/ except the two source
 * SVGs, then run this script to rebuild everything from scratch.
 *
 * Invocation:
 *   scripts/generate-icons.sh         (bash wrapper)
 *   scripts/generate-icons.ps1        (PowerShell wrapper)
 *
 * Both wrappers create scripts/generate-icons/ on demand (gitignored),
 * write a minimal package.json there, npm install sharp + png-to-ico,
 * then run this file with NODE_PATH pointing at the installed modules.
 * So the whole scripts/generate-icons/ folder is transient — safe to
 * delete at any time.
 *
 * Outputs (all written into images/):
 *   bowire_logo.png                 1024×~605, black on transparent
 *   bowire_logo.webp                same
 *   bowire_logo_small.png           256×256, black on transparent
 *   bowire_logo_small.webp          same
 *   favicon.ico                      multi-res 16 + 32 + 48 (browser)
 *   bowire.ico                      multi-res 16/20/24/32/40/48/64/96/128/256
 *                                    (Windows app icon — ICO spec caps at 256)
 *
 * The on-disk PNGs/WebPs render the SVG in its DEFAULT (black) state —
 * raster formats can't carry a media-query, so the white "mono" variant
 * is no longer pre-baked. Consumers that need a fixed-white raster (e.g.
 * dark-mode-only marketing screenshots) should re-render the SVG with a
 * forced fill, or rely on CSS that uses the theme-aware SVG.
 */

const fs = require('fs');
const path = require('path');
const sharp = require('sharp');
const pngToIco = require('png-to-ico');

// Resolve repo paths relative to this file so the script works when
// invoked from any cwd. scripts/generate-icons.js → ../ = repo root.
const repoRoot = path.resolve(__dirname, '..');
const imagesDir = path.join(repoRoot, 'images');

const LARGE_SRC = path.join(imagesDir, 'bowire_logo.svg');
const SMALL_SRC = path.join(imagesDir, 'bowire_logo_small.svg');

// Target width for the large horizontal logo. Height derives from the
// SVG viewBox ratio so the aspect stays correct.
const LARGE_WIDTH = 1024;

// Target size for the small square logo (and intermediate PNGs for the
// ICO bundles).
const SMALL_SIZE = 256;

// Multi-res ICO sizes.
// The ICO spec caps each embedded image at 256×256 — the directory
// header stores width/height as a single byte (0 means 256). Larger
// dimensions overflow and most readers fall back to one of the smaller
// frames anyway, so we don't pretend to carry 512+. For Windows Store
// tiles or web manifest icons that need 512+ px, ship a standalone PNG
// alongside the .ico.
const FAVICON_SIZES = [16, 32, 48];
const APP_ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

// --- SVG helpers ---

/**
 * Reads an SVG's width/height from the root `<svg width="…" height="…">`
 * attributes. Unitless or millimetre-suffixed values are both accepted.
 * Returns {width, height} as plain numbers (unit stripped).
 */
function readSvgDimensions(svgText) {
    const widthMatch = svgText.match(/<svg[^>]*\swidth="([\d.]+)(?:mm|px)?"/i);
    const heightMatch = svgText.match(/<svg[^>]*\sheight="([\d.]+)(?:mm|px)?"/i);
    if (!widthMatch || !heightMatch) {
        throw new Error('Could not read width/height from SVG root element');
    }
    return {
        width: parseFloat(widthMatch[1]),
        height: parseFloat(heightMatch[1]),
    };
}

// --- Raster helpers ---

/**
 * Renders a raw SVG buffer into a PNG/WebP file at the requested pixel
 * dimensions. `density: 300` tells librsvg to rasterise at 300 DPI
 * internally before sharp's resize step — high enough for crisp output
 * at 1024 px width, low enough to stay well under sharp's default pixel
 * limit. `limitInputPixels: false` turns off the guardrail entirely so
 * future large SVGs don't surprise us during builds; this is a build-
 * time script, so the extra memory is fine.
 */
async function renderSvg(svgBuffer, outPath, width, height, format) {
    let pipeline = sharp(svgBuffer, { density: 300, limitInputPixels: false })
        .resize(width, height, {
            fit: 'contain',
            background: { r: 0, g: 0, b: 0, alpha: 0 },
        });

    if (format === 'png') pipeline = pipeline.png({ compressionLevel: 9 });
    else if (format === 'webp') pipeline = pipeline.webp({ quality: 92 });
    else throw new Error(`Unknown format: ${format}`);

    await pipeline.toFile(outPath);
}

// --- Main ---

async function main() {
    if (!fs.existsSync(LARGE_SRC)) {
        throw new Error(`Missing source SVG: ${LARGE_SRC}`);
    }
    if (!fs.existsSync(SMALL_SRC)) {
        throw new Error(`Missing source SVG: ${SMALL_SRC}`);
    }

    // Load both source SVGs as text so we can read their dimensions.
    // The SVGs already carry a prefers-color-scheme media query so a
    // single file handles both light and dark contexts; no white-inverted
    // mono copies are written.
    const largeSvgText = fs.readFileSync(LARGE_SRC, 'utf8');
    const smallSvgText = fs.readFileSync(SMALL_SRC, 'utf8');

    const largeSvgBuf = Buffer.from(largeSvgText, 'utf8');
    const smallSvgBuf = Buffer.from(smallSvgText, 'utf8');

    const largeDims = readSvgDimensions(largeSvgText);
    const largeHeight = Math.round(LARGE_WIDTH * (largeDims.height / largeDims.width));

    console.log(`Large logo: ${LARGE_WIDTH}×${largeHeight} px (source ratio ${largeDims.width}×${largeDims.height})`);
    console.log(`Small logo: ${SMALL_SIZE}×${SMALL_SIZE} px`);

    // --- Large horizontal logo (hero / docs / website banner) ---
    await renderSvg(largeSvgBuf, path.join(imagesDir, 'bowire_logo.png'), LARGE_WIDTH, largeHeight, 'png');
    await renderSvg(largeSvgBuf, path.join(imagesDir, 'bowire_logo.webp'), LARGE_WIDTH, largeHeight, 'webp');
    console.log('Wrote large logo PNG + WebP.');

    // --- Small square logo (menu / docs / favicon base) ---
    await renderSvg(smallSvgBuf, path.join(imagesDir, 'bowire_logo_small.png'), SMALL_SIZE, SMALL_SIZE, 'png');
    await renderSvg(smallSvgBuf, path.join(imagesDir, 'bowire_logo_small.webp'), SMALL_SIZE, SMALL_SIZE, 'webp');
    console.log('Wrote small logo PNG + WebP.');

    // --- Favicons (multi-resolution ICO bundles) ---
    // png-to-ico wants a list of PNG files, each sized to one of the
    // resolutions the bundle should embed. Render every unique size
    // once into a temp dir and then compose both ICO files from
    // overlapping subsets of that set.
    const tempDir = fs.mkdtempSync(path.join(require('os').tmpdir(), 'bowire-ico-'));
    try {
        const allSizes = Array.from(new Set([...FAVICON_SIZES, ...APP_ICO_SIZES])).sort((a, b) => a - b);
        const sizedPaths = {};
        for (const size of allSizes) {
            const p = path.join(tempDir, `small_${size}.png`);
            await renderSvg(smallSvgBuf, p, size, size, 'png');
            sizedPaths[size] = p;
        }

        const faviconBuf = await pngToIco(FAVICON_SIZES.map(s => sizedPaths[s]));
        fs.writeFileSync(path.join(imagesDir, 'favicon.ico'), faviconBuf);
        console.log(`Wrote favicon.ico  (${FAVICON_SIZES.join(', ')})`);

        const appIcoBuf = await pngToIco(APP_ICO_SIZES.map(s => sizedPaths[s]));
        fs.writeFileSync(path.join(imagesDir, 'bowire.ico'), appIcoBuf);
        console.log(`Wrote bowire.ico  (${APP_ICO_SIZES.join(', ')})`);
    } finally {
        fs.rmSync(tempDir, { recursive: true, force: true });
    }

    console.log('\nDone. images/ rebuilt. Run distribute-icons.sh to push the new artefacts to consumers (wwwroot / docs / site).');
}

main().catch(err => {
    console.error('generate-icons failed:', err);
    process.exit(1);
});
