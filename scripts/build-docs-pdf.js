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

        // #241 — track the merged-PDF page index + the rendered page
        // <title> for every page so the outline tree can have both
        // top-level section bookmarks AND deeper-level page + heading
        // children. The reader's side-panel bookmarks become the
        // actual navigation primitive.
        const sectionStartPages = new Map();   // relUnix → mergedPageIndex (TOC parents)
        const pageStartIndices = new Map();    // relUnix → mergedPageIndex (every page)
        const pageTitles = new Map();          // relUnix → string (every page)
        const pageHeadings = new Map();        // relUnix → [{ tag, text, mergedPageIndex }, ...]
                                               // (h2/h3 anchors with their landing merged-page index)

        let count = 0;
        let tocInserted = false;
        for (const rel of pages) {
            // 0-based index where THIS rel will land in the merged PDF.
            // Normalise the path separator — collectPages emits OS-native
            // (`\\` on Windows) while TOC_ENTRIES use `/` (Unix style),
            // so a direct `===` would silently miss every entry on
            // Windows runners. Compare the forward-slash forms.
            const relUnix = rel.split(path.sep).join('/');
            const startIndex = merged.getPageCount();
            pageStartIndices.set(relUnix, startIndex);
            if (TOC_ENTRIES.some(e => e.file === relUnix)) {
                sectionStartPages.set(relUnix, startIndex);
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
                // Capture the rendered page's title for outline-tree
                // child labels. docfx wires `<title>` from front-matter
                // (or <h1>) and appends the site name with a pipe
                // separator ("Embedded Mode | Bowire Documentation").
                // Strip everything from the first ` | ` onward so the
                // bookmark label stays compact; the section name is
                // already implicit in the parent outline entry.
                try {
                    let t = (await page.title() || '').trim();
                    const sep = t.indexOf(' | ');
                    if (sep > 0) t = t.substring(0, sep).trim();
                    if (t) pageTitles.set(relUnix, t);
                } catch (_) { /* fall through to filename-derived title */ }

                // Extract h2 / h3 headings + their fractional Y in the
                // rendered HTML so we can map each one to the PDF sub-
                // page it lands on. scrollY-relative position is
                // independent of viewport — divide it by the document
                // scrollHeight to get a 0..1 fraction, then multiply by
                // the PDF's sub-page count to find which page the
                // heading falls in.
                let headingProbe = [];
                let docHeight = 1;
                try {
                    const probe = await page.evaluate(() => {
                        const headings = Array.from(
                            document.querySelectorAll('article h2, article h3')
                        ).map(h => ({
                            tag: h.tagName,
                            text: (h.textContent || '').trim(),
                            top: h.getBoundingClientRect().top + window.scrollY,
                        })).filter(h => h.text);
                        return {
                            headings,
                            docHeight: document.documentElement.scrollHeight,
                        };
                    });
                    headingProbe = probe.headings || [];
                    docHeight = probe.docHeight || 1;
                } catch (_) { /* page without article — skip */ }

                const buf = await page.pdf({
                    format: 'A4',
                    printBackground: true,
                    margin: { top: '18mm', bottom: '18mm', left: '14mm', right: '14mm' },
                });
                const sub = await PDFDocument.load(buf);
                const subPageCount = sub.getPageCount();
                const copied = await merged.copyPages(sub, sub.getPageIndices());
                copied.forEach(p => merged.addPage(p));

                // Now that we know how many PDF pages this HTML
                // produced, compute the merged-page-index for each
                // heading. fraction ≈ heading.top / docHeight; clamp
                // to [0, subPageCount-1] so a heading at the very end
                // doesn't index past the last page.
                if (headingProbe.length > 0 && subPageCount > 0) {
                    pageHeadings.set(relUnix, headingProbe.map(h => {
                        const frac = Math.max(0, Math.min(0.9999, h.top / docHeight));
                        const offset = Math.floor(frac * subPageCount);
                        return {
                            tag: h.tag,
                            text: h.text,
                            mergedPageIndex: startIndex + offset,
                        };
                    }));
                }

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
        buildOutlineTree(merged, sectionStartPages, pageStartIndices, pageTitles, pageHeadings);

        const bytes = await merged.save();
        fs.writeFileSync(OUT_PATH, bytes);
        log(`wrote ${OUT_PATH} (${(bytes.length / 1024).toFixed(0)} KB, ${merged.getPageCount()} pages from ${count} sources)`);
    } finally {
        await browser.close();
    }
}

/**
 * Hand-build a recursive (N-level) PDF 1.7 outline (bookmark) tree on
 * the merged document. pdf-lib doesn't expose a high-level outline API
 * — we mutate the catalog directly. Structure per spec:
 *
 *   Catalog /Outlines      -> ref to root outline dict
 *   Catalog /PageMode      -> /UseOutlines (auto-opens the bookmark panel)
 *   Root outline dict      -> { Type: 'Outlines', First, Last, Count }
 *   Each item dict         -> { Title (HexString), Parent, Dest [pageRef /Fit],
 *                               Prev?, Next?, First?, Last?, Count? }
 *
 * Hierarchy mirrors the docs directory tree:
 *   - Top-level = TOC_ENTRIES (Quickstart, Setup, User Guide, …)
 *   - Children of a section's index.html = every page directly under
 *     that section's directory PLUS the index.html of any sub-directory
 *   - Sub-directory's children recurse the same rule
 *
 * Counts use the PDF convention: negative Count keeps the item closed
 * by default (the reader expands what they want).
 */
function buildOutlineTree(merged, sectionStartPages, pageStartIndices, pageTitles, pageHeadings) {
    const usable = TOC_ENTRIES.filter(e => sectionStartPages.has(e.file));
    if (usable.length === 0) {
        log('  no sections matched TOC entries — outline tree skipped');
        return;
    }
    const ctx = merged.context;
    const pageRefs = merged.getPages().map(p => p.ref);

    function fallbackTitle(rel) {
        const base = rel.substring(rel.lastIndexOf('/') + 1).replace(/\.html$/, '');
        return base.replace(/[-_]/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
    }

    // Heading children for an individual page: each h2 (and h3 nested
    // under it) becomes a bookmark. The h2/h3 outline entries land at
    // the merged-PDF page where the heading falls (computed at render
    // time from its fractional Y in the HTML × subPageCount).
    function headingChildrenFor(file) {
        const hs = pageHeadings.get(file);
        if (!hs || hs.length === 0) return [];
        // Group: h2 = top-level of this page's tree, subsequent h3 fold
        // under the most recent h2. This mirrors the natural reading
        // hierarchy without needing to compute deep h3-only chains.
        const nodes = [];
        let cur = null;
        for (const h of hs) {
            const item = {
                rel: null,        // synthetic — no underlying file
                title: h.text,
                startIdx: h.mergedPageIndex,
                children: [],
            };
            if (h.tag === 'H3' && cur) {
                cur.children.push(item);
            } else {
                nodes.push(item);
                cur = item;
            }
        }
        return nodes;
    }

    // Build a tree node for a given .html file. If the file is an
    // index.html, its directory's other pages + sub-directory
    // index.htmls become its children (recursively). Non-index files
    // are leaves but still get their in-page h2/h3 headings folded in.
    function buildNode(file, customTitle) {
        const node = {
            rel: file,
            title: customTitle || pageTitles.get(file) || fallbackTitle(file),
            startIdx: pageStartIndices.get(file),
            children: [],
        };

        if (!file.endsWith('/index.html') && file !== 'index.html') {
            // Non-section page → leaf at the file level, but expose
            // its h2/h3 headings as sub-bookmarks so deep documents
            // (e.g. ui-guide/action-bar.html → Execute Button) are
            // navigable from the side panel.
            node.children = headingChildrenFor(file);
            return node;
        }

        const dir = file === 'index.html'
            ? ''
            : file.substring(0, file.length - 'index.html'.length);

        // Top-level (cover) has no children — TOC_ENTRIES already
        // enumerate the top-level sections separately.
        if (dir === '') return node;

        const direct = [];                       // rel-paths of immediate child pages
        const subSections = new Map();           // subDirName → its index.html path
        for (const rel of pageStartIndices.keys()) {
            if (rel === file) continue;
            if (!rel.startsWith(dir)) continue;
            const sub = rel.substring(dir.length);
            if (!sub.includes('/')) {
                direct.push(rel);
            } else {
                const sdName = sub.substring(0, sub.indexOf('/'));
                const sdIndex = dir + sdName + '/index.html';
                if (pageStartIndices.has(sdIndex) && !subSections.has(sdName)) {
                    subSections.set(sdName, sdIndex);
                }
            }
        }
        const childRels = [...direct, ...subSections.values()];
        // Sort by reading order — that's the source-walked sequence
        // collectPages produced, captured in pageStartIndices.
        childRels.sort((a, b) => pageStartIndices.get(a) - pageStartIndices.get(b));
        const fileChildren = childRels.map(c => buildNode(c));
        // The section's own index.html may have h2/h3 too — prepend
        // them so they precede the sub-page list.
        node.children = [...headingChildrenFor(file), ...fileChildren];
        return node;
    }

    const topNodes = usable.map(e => buildNode(e.file, e.title));

    // Two-pass emit: first assign a PDFRef to every node so siblings
    // and parents can reference each other up-front; then create
    // each dict with the full wiring.
    const outlinesRef = ctx.nextRef();
    function assignRefs(node) {
        node.ref = ctx.nextRef();
        node.children.forEach(assignRefs);
    }
    topNodes.forEach(assignRefs);

    function emitNode(node, parentRef, siblings, indexInSiblings) {
        const dict = ctx.obj({
            Title: PDFHexString.fromText(node.title),
            Parent: parentRef,
            Dest: [pageRefs[node.startIdx], PDFName.of('Fit')],
        });
        if (indexInSiblings > 0) dict.set(PDFName.of('Prev'), siblings[indexInSiblings - 1].ref);
        if (indexInSiblings < siblings.length - 1) dict.set(PDFName.of('Next'), siblings[indexInSiblings + 1].ref);
        if (node.children.length > 0) {
            dict.set(PDFName.of('First'), node.children[0].ref);
            dict.set(PDFName.of('Last'), node.children[node.children.length - 1].ref);
            // Negative Count = item closed by default. Per PDF 1.7
            // spec the magnitude is the descendants visible IF it
            // were open. With everything-collapsed mode, opening a
            // node reveals only its immediate children (themselves
            // still collapsed), so the magnitude is children.length.
            dict.set(PDFName.of('Count'), PDFNumber.of(-node.children.length));
        }
        ctx.assign(node.ref, dict);

        node.children.forEach((c, i) => emitNode(c, node.ref, node.children, i));
    }
    topNodes.forEach((tn, i) => emitNode(tn, outlinesRef, topNodes, i));

    const outlinesDict = ctx.obj({ Type: 'Outlines' });
    outlinesDict.set(PDFName.of('First'), topNodes[0].ref);
    outlinesDict.set(PDFName.of('Last'), topNodes[topNodes.length - 1].ref);
    // Root /Count = visible items across all levels at first paint.
    // Everything collapsed → only the top-level entries are visible.
    outlinesDict.set(PDFName.of('Count'), PDFNumber.of(topNodes.length));
    ctx.assign(outlinesRef, outlinesDict);

    merged.catalog.set(PDFName.of('Outlines'), outlinesRef);
    merged.catalog.set(PDFName.of('PageMode'), PDFName.of('UseOutlines'));

    function tally(nodes) {
        return nodes.reduce((sum, n) => sum + 1 + tally(n.children), 0);
    }
    const total = tally(topNodes);
    log(`  outline tree: ${topNodes.length} sections + ${total - topNodes.length} descendants wired`);
}

main().catch(err => {
    console.error(`[pdf] FAILED: ${err.message}`);
    if (err.stack) console.error(err.stack);
    process.exit(1);
});
