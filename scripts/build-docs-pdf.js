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
const { PDFDocument, PDFName, PDFNumber, PDFHexString } = require('pdf-lib');

const ROOT = path.resolve(__dirname, '..');
const SRC = path.join(ROOT, 'artifacts', 'docs-standalone');
const OUT_DIR = path.join(ROOT, 'publish', 'archives');
const OUT_PATH = path.join(OUT_DIR, 'bowire-docs.pdf');

// Bowire version + build-date the cover badge embeds. Pulled from
// Directory.Build.props — same source the release pipeline reads,
// so the badge always reflects the floor of the build that
// rendered the PDF. Both fall through to placeholders when the
// build runs from a stale checkout.
function readBuildVersion() {
    try {
        const propsPath = path.join(ROOT, 'Directory.Build.props');
        const xml = fs.readFileSync(propsPath, 'utf-8');
        const match = /<Version>([^<]+)<\/Version>/.exec(xml);
        if (match && match[1]) {
            // Strip a "-dev" floor suffix — the published PDF carries
            // the released version, not the in-progress dev floor.
            return match[1].replace(/-dev$/, '');
        }
    } catch (err) {
        log(`could not read Directory.Build.props (${err.message}) — falling back to placeholder version`);
    }
    return 'unreleased';
}
const BUILD_VERSION = readBuildVersion();
const BUILD_DATE = new Date().toISOString().slice(0, 10);

// Top-level table of contents, hand-curated to mirror the section
// ordering below. Rendered as a standalone page after the cover so
// the PDF opens like a real book — cover, TOC, content. Sub-sections
// stay implicit; for a paginated TOC with page numbers we'd need a
// two-pass render (collect → renumber → emit) — the curated list is
// the cheaper 80%-good option.
const TOC_ENTRIES = [
    { title: 'Quickstart', file: 'quickstart.html' },
    { title: 'Use cases', file: 'use-cases.html' },
    { title: 'Setup', file: 'setup/index.html' },
    { title: 'User Guide', file: 'ui-guide/index.html' },
    { title: 'Features', file: 'features/index.html' },
    { title: 'Protocol guides', file: 'protocols/index.html' },
    { title: 'Architecture', file: 'architecture/index.html' },
    { title: 'API Reference', file: 'api/index.html' },
];

function buildTocHtml() {
    const rows = TOC_ENTRIES.map(e =>
        `<li><a href="${e.file}">${escapeHtml(e.title)}</a></li>`
    ).join('\n        ');
    return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Bowire Documentation — Table of Contents</title>
<style>
    body {
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
        color: #0a0a0a;
        padding: 24mm 18mm;
        margin: 0;
    }
    h1 {
        font-size: 28pt;
        font-weight: 700;
        letter-spacing: -0.02em;
        margin: 0 0 0.5em;
    }
    .subtitle {
        color: #6b6b6b;
        font-size: 12pt;
        margin: 0 0 2em;
    }
    ol {
        list-style: none;
        counter-reset: toc;
        padding: 0;
        margin: 0;
    }
    ol li {
        counter-increment: toc;
        font-size: 14pt;
        padding: 10px 0;
        border-bottom: 1px solid #e3e3e3;
        display: flex;
        align-items: baseline;
        gap: 12px;
    }
    ol li::before {
        content: counter(toc, decimal-leading-zero);
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11pt;
        color: #999;
        min-width: 28px;
    }
    ol li a {
        color: #0a0a0a;
        text-decoration: none;
    }
</style>
</head>
<body>
    <h1>Table of Contents</h1>
    <p class="subtitle">Bowire ${escapeHtml(BUILD_VERSION)} · ${BUILD_DATE}</p>
    <ol>
        ${rows}
    </ol>
</body>
</html>`;
}

function escapeHtml(s) {
    return String(s)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

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

        // #241 — track the merged-PDF page index at which each TOC
        // entry's source file lands so we can build a proper outline
        // tree after the merge. The reader's side-panel bookmarks
        // become the actual navigation primitive.
        const sectionStartPages = new Map();

        let count = 0;
        let tocInserted = false;
        for (const rel of pages) {
            // 0-based index where THIS rel will land in the merged PDF.
            // Normalise the path separator — collectPages emits OS-native
            // (`\\` on Windows) while TOC_ENTRIES use `/` (Unix style),
            // so a direct `===` would silently miss every entry on
            // Windows runners. Compare the forward-slash forms.
            const relUnix = rel.split(path.sep).join('/');
            if (TOC_ENTRIES.some(e => e.file === relUnix)) {
                sectionStartPages.set(relUnix, merged.getPageCount());
            }
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
                // Fill the cover-badge placeholder with the actual
                // version + build date. The badge in docs/index.md
                // ships with `data-bowire-version-value` set to '—'
                // so the online HTML doesn't pretend to know its
                // version; the PDF pass overwrites it.
                await page.evaluate(({ version, date, isCover }) => {
                    const el = document.querySelector('[data-bowire-version-value]');
                    if (el) el.textContent = `${version} · ${date}`;
                    // On the cover page the small standalone header
                    // ("Bowire Documentation" pill) duplicates the
                    // huge hero title right below it. Hide the
                    // top-bar so the cover reads as one composition
                    // instead of as a logo + title repeated.
                    if (isCover) {
                        const hdr = document.getElementById('bowire-docs-header');
                        if (hdr) hdr.style.display = 'none';
                    }
                }, { version: BUILD_VERSION, date: BUILD_DATE, isCover: rel === 'index.html' });
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

            // Right after the cover (index.html) is in the merged
            // PDF, inject the curated TOC page so the document opens
            // like a real book — cover, table of contents, content.
            // The TOC is rendered through Playwright with setContent
            // so it shares the same A4 / margin / font setup.
            if (!tocInserted && rel === 'index.html') {
                const tocPage = await ctx.newPage();
                try {
                    await tocPage.setContent(buildTocHtml(), { waitUntil: 'load' });
                    const tocBuf = await tocPage.pdf({
                        format: 'A4',
                        printBackground: true,
                        margin: { top: '18mm', bottom: '18mm', left: '14mm', right: '14mm' },
                    });
                    const tocDoc = await PDFDocument.load(tocBuf);
                    const tocCopied = await merged.copyPages(tocDoc, tocDoc.getPageIndices());
                    tocCopied.forEach(p => merged.addPage(p));
                    tocInserted = true;
                    log('  TOC page rendered + inserted after cover');
                } finally {
                    await tocPage.close();
                }
            }
        }
        await ctx.close();

        // #241 — Attach a /Outlines tree to the merged PDF so Acrobat /
        // Preview / Sumatra / Foxit show clickable section bookmarks
        // in the side panel. That's the actual TOC navigation primitive
        // in PDFs — the front-matter TOC sheet is informational only.
        buildOutlineTree(merged, sectionStartPages);

        const bytes = await merged.save();
        fs.writeFileSync(OUT_PATH, bytes);
        log(`wrote ${OUT_PATH} (${(bytes.length / 1024).toFixed(0)} KB, ${merged.getPageCount()} pages from ${count} sources)`);
    } finally {
        await browser.close();
    }
}

/**
 * Hand-build a PDF 1.7 outline (bookmark) tree on the merged document.
 * pdf-lib doesn't expose a high-level outline API — we mutate the
 * catalog directly. Structure per spec:
 *
 *   Catalog /Outlines      -> ref to root outline dict
 *   Catalog /PageMode      -> /UseOutlines (auto-opens the bookmark panel)
 *   Root outline dict      -> { Type: 'Outlines', First, Last, Count }
 *   Each item dict         -> { Title (HexString), Parent: root, Dest: [pageRef, /Fit], Prev?, Next? }
 *
 * One top-level entry per curated section in TOC_ENTRIES; their
 * destinations are the merged-PDF page indices captured in
 * sectionStartPages during the render loop.
 */
function buildOutlineTree(merged, sectionStartPages) {
    const usable = TOC_ENTRIES.filter(e => sectionStartPages.has(e.file));
    if (usable.length === 0) {
        log('  no sections matched TOC entries — outline tree skipped');
        return;
    }
    const ctx = merged.context;
    const pageRefs = merged.getPages().map(p => p.ref);

    const outlinesRef = ctx.nextRef();
    const itemRefs = usable.map(() => ctx.nextRef());

    usable.forEach((entry, i) => {
        const startPage = sectionStartPages.get(entry.file);
        const pageRef = pageRefs[startPage];
        const item = ctx.obj({
            Title: PDFHexString.fromText(entry.title),
            Parent: outlinesRef,
            Dest: [pageRef, PDFName.of('Fit')],
        });
        if (i > 0) item.set(PDFName.of('Prev'), itemRefs[i - 1]);
        if (i < itemRefs.length - 1) item.set(PDFName.of('Next'), itemRefs[i + 1]);
        ctx.assign(itemRefs[i], item);
    });

    const outlinesDict = ctx.obj({
        Type: 'Outlines',
    });
    outlinesDict.set(PDFName.of('First'), itemRefs[0]);
    outlinesDict.set(PDFName.of('Last'), itemRefs[itemRefs.length - 1]);
    outlinesDict.set(PDFName.of('Count'), PDFNumber.of(itemRefs.length));
    ctx.assign(outlinesRef, outlinesDict);

    merged.catalog.set(PDFName.of('Outlines'), outlinesRef);
    merged.catalog.set(PDFName.of('PageMode'), PDFName.of('UseOutlines'));
    log(`  outline tree: ${itemRefs.length} section bookmarks wired`);
}

main().catch(err => {
    console.error(`[pdf] FAILED: ${err.message}`);
    if (err.stack) console.error(err.stack);
    process.exit(1);
});
