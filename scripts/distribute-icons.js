#!/usr/bin/env node
/**
 * Bowire icon distributor.
 *
 * Copies the current icon artefacts from images/ into every consumer
 * location (embedded UI / docfx docs / marketing site) so the three
 * surfaces stay in lockstep with the author-owned sources. Also
 * renders one extra PNG that doesn't exist in images/ — the iOS
 * apple-touch-icon (180×180) used for home-screen bookmarks.
 *
 * This script is strictly a copy/render step. It never touches
 * images/ itself — run `generate-icons.sh` first if the sources in
 * images/ need rebuilding.
 *
 * Invocation:
 *   scripts/distribute-icons.sh      (bash wrapper)
 *   scripts/distribute-icons.ps1     (PowerShell wrapper)
 *
 * Dependencies (sharp for the one PNG render) share the transient
 * scripts/generate-icons/ folder with generate-icons.js. First run
 * triggers the inline package.json write + npm install; subsequent
 * runs are instant.
 *
 * Consumer locations written:
 *   src/Kuestenlogik.Bowire/wwwroot/favicon.svg    ← images/bowire_logo_small.svg
 *   docs/favicon.ico                      ← images/favicon.ico
 *   site/assets/images/favicon.ico        ← images/favicon.ico
 *   site/assets/images/bowire-logo.svg   ← images/bowire_logo.svg
 *   site/assets/images/apple-touch-icon.png  ← 180×180 PNG of small SVG
 *
 * NOT distributed: site/assets/images/favicon.svg + the equivalent
 * docs DocFX favicon at docs/templates/bowire/public/favicon.svg.
 * Those two share a "branded tile" design (indigo background + white
 * Circle-B) that's independent of the bare B-glyph in images/. They
 * stay manually edited together when the brand changes.
 */

const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const repoRoot = path.resolve(__dirname, '..');
const imagesDir = path.join(repoRoot, 'images');

// Plain file copies. Every entry is just `<images/ relative src>` →
// `<repo relative dest>`. Directories are auto-created.
const COPIES = [
    // Embedded Bowire web UI — BowireHtmlGenerator.FaviconDataUrl
    // reads this file at build time and inlines it as a data URL into
    // the HTML (browser tab + topbar brand logo via config.logoIcon).
    { src: 'bowire_logo_small.svg', dest: 'src/Kuestenlogik.Bowire/wwwroot/favicon.svg' },

    // docfx documentation — browser tab icon.
    { src: 'favicon.ico', dest: 'docs/favicon.ico' },

    // Marketing site (Jekyll). favicon.svg is intentionally NOT
    // distributed — the site shares the branded-tile design with the
    // DocFX docs site (rounded indigo background + white Circle-B).
    // Source of truth for that variant lives at
    // docs/templates/bowire/public/favicon.svg; site/assets/images/
    // favicon.svg is kept byte-identical via manual sync.
    { src: 'favicon.ico', dest: 'site/assets/images/favicon.ico' },
    { src: 'bowire_logo.svg', dest: 'site/assets/images/bowire-logo.svg' },
];

// SVG → PNG renders for consumer paths that don't have a matching file
// in images/. Currently only the iOS apple-touch-icon (180×180).
const RENDERS = [
    {
        src: 'bowire_logo_small.svg',
        dest: 'site/assets/images/apple-touch-icon.png',
        size: 180,
    },
];

async function renderPng(srcPath, destPath, size) {
    await sharp(srcPath, { density: 300, limitInputPixels: false })
        .resize(size, size, {
            fit: 'contain',
            background: { r: 0, g: 0, b: 0, alpha: 0 },
        })
        .png({ compressionLevel: 9 })
        .toFile(destPath);
}

async function main() {
    // Sanity check: images/ must already be populated by generate-
    // icons.js. If the files aren't there, bail with a pointer.
    const required = [
        ...COPIES.map(c => c.src),
        ...RENDERS.map(r => r.src),
    ];
    for (const f of new Set(required)) {
        if (!fs.existsSync(path.join(imagesDir, f))) {
            console.error(`Missing images/${f} — run scripts/generate-icons.sh first.`);
            process.exit(1);
        }
    }

    for (const item of COPIES) {
        const srcPath = path.join(imagesDir, item.src);
        const destPath = path.join(repoRoot, item.dest);
        fs.mkdirSync(path.dirname(destPath), { recursive: true });
        fs.copyFileSync(srcPath, destPath);
        console.log(`Copied   ${item.src} → ${item.dest}`);
    }

    for (const item of RENDERS) {
        const srcPath = path.join(imagesDir, item.src);
        const destPath = path.join(repoRoot, item.dest);
        fs.mkdirSync(path.dirname(destPath), { recursive: true });
        await renderPng(srcPath, destPath, item.size);
        console.log(`Rendered ${item.src} → ${item.dest}  (${item.size}×${item.size} PNG)`);
    }

    console.log('\nDone. Consumers synced with images/.');
}

main().catch(err => {
    console.error('distribute-icons failed:', err);
    process.exit(1);
});
