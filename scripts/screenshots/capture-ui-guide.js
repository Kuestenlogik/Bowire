/**
 * Captures the docs/ui-guide/ companion screenshots. Each surface is
 * captured in BOTH themes so the docs pages'
 * `<img class="theme-img-dark">` / `<img class="theme-img-light">`
 * pair-tag pattern keeps working.
 *
 * Output: docs/images/ui-guide/<name>-{dark,light}.png
 *
 * Operator MUST already have these processes running (operator-managed):
 *   http://localhost:5180          Tool standalone
 *   https://localhost:5101/bowire  Combined Harbor sample
 *   http://localhost:5181/bowire   Sample.Embedded (REST + Map)
 *
 * Each capture is wrapped in try/catch — one failure doesn't kill the
 * next. The script returns 0 if at least one PNG landed.
 */

const path = require('path');
const fs = require('fs');
// Canonical sidebar helpers — see scripts/lib/sidebar.cjs (#551).
const sidebar = require('../lib/sidebar.cjs');
// Crop from the element's own box, not from coordinates measured by eye.
const clipper = require('./_region-clip.js');

let chromium;
try { chromium = require('@playwright/test').chromium; }
catch (e) {
    console.error('[bail] @playwright/test not available:', e.message);
    process.exit(2);
}

const OUT = path.resolve(__dirname, '..', '..', 'docs', 'images', 'ui-guide');
fs.mkdirSync(OUT, { recursive: true });

const COMBINED_BOWIRE = 'https://localhost:5101/bowire';
const COMBINED_ROOT   = 'https://localhost:5101/';
const WIDTH = 1400;
const HEIGHT = 900;
const DSF = 2;

function log(msg) { console.log(new Date().toISOString().slice(11, 19), msg); }
function outPath(name, theme) { return path.join(OUT, name + '-' + theme + '.png'); }

async function seed(page, sampleUrl, railMode, theme) {
    await page.evaluate(({ sampleUrl, railMode, theme }) => {
        try {
            const id = 'harbor';
            localStorage.setItem('bowire_workspaces', JSON.stringify([{
                id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000
            }]));
            localStorage.setItem('bowire_active_workspace_id', id);
            localStorage.setItem('bowire_ws_' + id + '_server_urls', JSON.stringify([sampleUrl]));
            localStorage.setItem('bowire_rail_mode', railMode);
            localStorage.setItem('bowire_theme_pref', theme);
        } catch { /* ignore */ }
    }, { sampleUrl, railMode, theme });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
}

/** Full workbench shot — anchor screenshot on index.md. */
async function captureWorkbench(page, theme) {
    log(`workbench [${theme}]: navigate + seed`);
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await sidebar.waitForCatalogue(page, { timeout: 30000 });
    await page.waitForTimeout(800);
    await sidebar.openCatalogue(page, { timeout: 30000 });
    await page.screenshot({ path: outPath('workbench', theme) });
    log(`workbench [${theme}]: OK`);
}

/** Rail strip column — clip to the 56 px-wide rail. */
async function captureRailStrip(page, theme) {
    log(`rail-strip [${theme}]: navigate + seed`);
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await page.waitForSelector('#bowire-activity-rail .bowire-rail-btn', { timeout: 15000 });
    await page.waitForTimeout(600);
    const railRect = await clipper.shootSurface(page, 'rail-strip', outPath('rail-strip', theme), { pad: 4 });
    log(`rail-strip [${theme}]: ${railRect ? `OK (${railRect.sel})` : 'SKIPPED — no element matched'}`);
}

/** Sidebar — Discover mode, services tree expanded, clip to the
 *  sidebar column (between the rail strip and the request pane). */
async function captureSidebar(page, theme) {
    log(`sidebar [${theme}]: navigate + seed`);
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await sidebar.waitForCatalogue(page, { timeout: 30000 });
    await page.waitForTimeout(600);
    await sidebar.openCatalogue(page, { timeout: 30000 });
    await page.waitForTimeout(400);
    const sideRect = await clipper.shootSurface(page, 'sidebar', outPath('sidebar', theme), { pad: 4 });
    log(`sidebar [${theme}]: ${sideRect ? `OK (${sideRect.sel})` : 'SKIPPED — no element matched'}`);
}

/** Request pane — pick a method first so the pane has content. */
async function captureRequestPane(page, theme) {
    log(`request-pane [${theme}]: navigate + seed`);
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await sidebar.waitForCatalogue(page, { timeout: 30000 });
    await page.waitForTimeout(600);
    await sidebar.openCatalogue(page, { timeout: 30000 });
    const m = page.locator('.bowire-method-item').first();
    await m.click();
    await page.waitForTimeout(800);
    const reqRect = await clipper.shootSurface(page, 'request-pane', outPath('request-pane', theme), { pad: 4 });
    log(`request-pane [${theme}]: ${reqRect ? `OK (${reqRect.sel})` : 'SKIPPED — no element matched'}`);
}

/** Response pane — execute first so the JSON viewer has content. */
async function captureResponsePane(page, theme) {
    log(`response-pane [${theme}]: navigate + seed`);
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await sidebar.waitForCatalogue(page, { timeout: 30000 });
    await page.waitForTimeout(600);
    await sidebar.openCatalogue(page, { timeout: 30000 });
    const list = page.locator('.bowire-method-item', { hasText: 'List' }).first();
    if (!(await list.isVisible().catch(() => false))) {
        const fallback = page.locator('.bowire-method-item').first();
        await fallback.click();
    } else {
        await list.click();
    }
    await page.waitForTimeout(600);
    const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
    if (await exec.isEnabled().catch(() => false)) {
        await exec.click();
        await page.waitForTimeout(1500);
    }
    const resRect = await clipper.shootSurface(page, 'response-pane', outPath('response-pane', theme), { pad: 4 });
    log(`response-pane [${theme}]: ${resRect ? `OK (${resRect.sel})` : 'SKIPPED — no element matched'}`);
}

/** Action bar — same setup as response pane, clip to the bottom strip. */
async function captureActionBar(page, theme) {
    log(`action-bar [${theme}]: navigate + seed`);
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await sidebar.waitForCatalogue(page, { timeout: 30000 });
    await page.waitForTimeout(600);
    await sidebar.openCatalogue(page, { timeout: 30000 });
    const m = page.locator('.bowire-method-item').first();
    await m.click();
    await page.waitForTimeout(800);
    const abRect = await clipper.shootSurface(page, 'action-bar', outPath('action-bar', theme), { pad: 8 });
    log(`action-bar [${theme}]: ${abRect ? `OK (${abRect.sel})` : 'SKIPPED — no element matched'}`);
}

const surfaces = [
    { name: 'workbench',     fn: captureWorkbench    },
    { name: 'rail-strip',    fn: captureRailStrip    },
    { name: 'sidebar',       fn: captureSidebar      },
    { name: 'request-pane',  fn: captureRequestPane  },
    { name: 'response-pane', fn: captureResponsePane },
    { name: 'action-bar',    fn: captureActionBar    }
];

(async () => {
    log('Launching Chromium…');
    const browser = await chromium.launch({
        headless: true,
        args: ['--ignore-certificate-errors']
    });

    const results = [];
    for (const theme of ['dark', 'light']) {
        const context = await browser.newContext({
            viewport: { width: WIDTH, height: HEIGHT },
            deviceScaleFactor: DSF,
            ignoreHTTPSErrors: true,
            colorScheme: theme
        });
        const page = await context.newPage();
        await page.emulateMedia({ colorScheme: theme });

        for (const s of surfaces) {
            try {
                await s.fn(page, theme);
                results.push({ name: s.name, theme, ok: true });
            } catch (e) {
                log(`[FAIL ${s.name} ${theme}] ${e.message}`);
                results.push({ name: s.name, theme, ok: false, err: e.message });
            }
        }
        await context.close();
    }
    await browser.close();

    log('Summary:');
    for (const r of results) {
        log(`  ${r.ok ? 'OK  ' : 'FAIL'} ${r.name}-${r.theme}${r.err ? ' — ' + r.err : ''}`);
    }
    const okCount = results.filter(r => r.ok).length;
    log(`${okCount}/${results.length} surfaces captured.`);
    process.exit(okCount > 0 ? 0 : 1);
})().catch(e => {
    console.error('[fatal]', e && e.stack || e);
    process.exit(1);
});
