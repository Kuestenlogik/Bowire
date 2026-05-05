/**
 * Builds bowire-docs.pdf from the standalone DocFX HTML output.
 *
 * Why custom instead of `docfx pdf`:
 *   The DocFX 2.x pdf command bundles Microsoft.Playwright internally
 *   and auto-installs Chromium on first use. The auto-install path
 *   races on CI (network hiccups + concurrent runs), produces no
 *   useful diagnostic when it fails, and writes the output to a
 *   path that depends on globalMetadata fields not all builds set.
 *   This script does the same job with the @playwright/test browser
 *   we already use for screenshot capture, gives us deterministic
 *   ordering, and fails loudly when something is missing.
 *
 * Usage:
 *   node scripts/build-docs-pdf.js
 *
 * Inputs:
 *   artifacts/docs-standalone/  — HTML tree from `docfx docs/docfx.standalone.json`
 *
 * Outputs:
 *   publish/archives/bowire-docs.pdf
 */
const { chromium } = require('@playwright/test');
const fs = require('fs');
const path = require('path');
const { PDFDocument } = require('pdf-lib');

const ROOT = path.resolve(__dirname, '..');
const SRC = path.join(ROOT, 'artifacts', 'docs-standalone');
const OUT_DIR = path.join(ROOT, 'publish', 'archives');
const OUT_PATH = path.join(OUT_DIR, 'bowire-docs.pdf');

// Logical reading order. Pages outside this prefix list are appended at
// the end in alphabetical order so nothing gets dropped silently when a
// new section lands.
const SECTION_ORDER = [
    'index.html',
    'quickstart.html',
    'use-cases.html',
    'setup',
    'ui-guide',
    'features',
    'protocols',
    'architecture',
    'api',
];

function log(msg) { console.log(`[pdf] ${msg}`); }

function collectPages(rootDir) {
    const pages = [];
    walk(rootDir, '', pages);

    // Sort: section-order prefix first (in defined order), unknown
    // sections appended alphabetically. Within a section, alphabetical
    // by relative path.
    const orderIndex = (relPath) => {
        for (let i = 0; i < SECTION_ORDER.length; i++) {
            const seg = SECTION_ORDER[i];
            if (relPath === seg || relPath.startsWith(seg + path.sep)) return i;
        }
        return SECTION_ORDER.length; // unknown → goes last
    };

    pages.sort((a, b) => {
        const ai = orderIndex(a);
        const bi = orderIndex(b);
        if (ai !== bi) return ai - bi;
        return a.localeCompare(b);
    });

    return pages;
}

function walk(rootDir, relPath, list) {
    const absDir = path.join(rootDir, relPath);
    const entries = fs.readdirSync(absDir, { withFileTypes: true });
    for (const ent of entries) {
        const childRel = relPath ? path.join(relPath, ent.name) : ent.name;
        if (ent.isDirectory()) {
            // Skip DocFX internals and any pre-existing _pdf folder.
            if (ent.name.startsWith('_')) continue;
            walk(rootDir, childRel, list);
        } else if (ent.isFile()
                && ent.name.endsWith('.html')
                && ent.name !== 'toc.html'
                && ent.name !== '404.html') {
            list.push(childRel);
        }
    }
}

async function main() {
    if (!fs.existsSync(SRC)) {
        throw new Error(`expected DocFX standalone output at ${SRC} — run \`docfx docs/docfx.standalone.json\` first`);
    }
    fs.mkdirSync(OUT_DIR, { recursive: true });

    const pages = collectPages(SRC);
    if (pages.length === 0) {
        throw new Error(`no HTML pages found under ${SRC}`);
    }
    log(`rendering ${pages.length} pages from ${SRC}`);

    const browser = await chromium.launch({ headless: true });
    try {
        const merged = await PDFDocument.create();
        const ctx = await browser.newContext({ viewport: { width: 1200, height: 900 } });

        let count = 0;
        for (const rel of pages) {
            const url = 'file://' + path.join(SRC, rel).split(path.sep).join('/');
            const page = await ctx.newPage();
            try {
                await page.goto(url, { waitUntil: 'networkidle', timeout: 30000 });
                // Force light theme for print — dark backgrounds waste
                // ink and most readers expect doc PDFs to render light.
                await page.evaluate(() => {
                    try { localStorage.setItem('bowire-theme', 'light'); } catch (_) {}
                    document.documentElement.setAttribute('data-theme', 'light');
                });
                await page.reload({ waitUntil: 'networkidle', timeout: 30000 });
                const buf = await page.pdf({
                    format: 'A4',
                    printBackground: true,
                    margin: { top: '18mm', bottom: '18mm', left: '14mm', right: '14mm' },
                });
                const sub = await PDFDocument.load(buf);
                const copied = await merged.copyPages(sub, sub.getPageIndices());
                copied.forEach(p => merged.addPage(p));
                count++;
                if (count % 10 === 0) log(`  ${count}/${pages.length} merged`);
            } finally {
                await page.close();
            }
        }
        await ctx.close();

        const bytes = await merged.save();
        fs.writeFileSync(OUT_PATH, bytes);
        log(`wrote ${OUT_PATH} (${(bytes.length / 1024).toFixed(0)} KB, ${merged.getPageCount()} pages from ${count} sources)`);
    } finally {
        await browser.close();
    }
}

main().catch(err => {
    console.error(`[pdf] FAILED: ${err.message}`);
    if (err.stack) console.error(err.stack);
    process.exit(1);
});
