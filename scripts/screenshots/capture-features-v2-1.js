/**
 * Captures the per-page screenshots embedded in docs/features/*.md.
 *
 * Output:  docs/images/screenshots/<name>.png
 *
 * Operator MUST already have these processes running:
 *   http://localhost:5180          Tool standalone (workbench at /)
 *   https://localhost:5101/bowire  Combined Harbor sample
 *   http://localhost:5181/bowire   Sample.Embedded (REST + Map)
 *
 * Partial success is success: each capture is wrapped in try/catch so
 * one failure doesn't kill the next. The script returns 0 as long as
 * at least one PNG landed.
 */

const path = require('path');
const fs = require('fs');

let chromium;
try {
    chromium = require('@playwright/test').chromium;
} catch (e) {
    console.error('[bail] @playwright/test not available:', e.message);
    process.exit(2);
}

const OUT = path.resolve(__dirname, '..', '..', 'docs', 'images', 'screenshots');
fs.mkdirSync(OUT, { recursive: true });

const TOOL = 'http://localhost:5180';
const COMBINED_BOWIRE = 'https://localhost:5101/bowire';
const COMBINED_ROOT = 'https://localhost:5101/';
const EMBEDDED_BOWIRE = 'http://localhost:5181/bowire';
const EMBEDDED_ROOT = 'http://localhost:5181/';

const WIDTH = 1400;
const HEIGHT = 900;
const DSF = 2;

function log(msg) { console.log(new Date().toISOString().slice(11, 19), msg); }
function outPath(name) { return path.join(OUT, name + '.png'); }

async function waitForAny(page, selectors, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        for (const sel of selectors) {
            const loc = page.locator(sel).first();
            if (await loc.isVisible().catch(() => false)) return sel;
        }
        await page.waitForTimeout(150);
    }
    return null;
}

async function seed(page, sampleUrl, railMode, opts) {
    railMode = railMode || 'discover';
    opts = opts || {};
    await page.evaluate(({ sampleUrl, railMode, opts }) => {
        try {
            const id = 'harbor';
            localStorage.setItem('bowire_workspaces', JSON.stringify([
                { id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000 },
                { id: 'acme', name: 'Acme staging', color: 'emerald', createdAt: 1_700_100_000_000 },
                { id: 'globex', name: 'Globex prod', color: 'rose', createdAt: 1_700_200_000_000 }
            ]));
            localStorage.setItem('bowire_active_workspace_id', id);
            localStorage.setItem('bowire_ws_' + id + '_server_urls', JSON.stringify([sampleUrl]));
            localStorage.setItem('bowire_rail_mode', railMode);
            localStorage.setItem('bowire_theme_pref', 'dark');
            if (opts.collections) {
                localStorage.setItem('bowire_ws_' + id + '_collections', JSON.stringify(opts.collections));
            }
        } catch { /* ignore */ }
    }, { sampleUrl, railMode, opts });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
}

/** Seeds workspace state then forces a /-rooted reload (Tool standalone). */
async function seedAtRoot(page, sampleUrl, railMode, opts) {
    return seed(page, sampleUrl, railMode, opts);
}

async function expandAllServices(page) {
    const groups = await page.locator('.bowire-service-group').all();
    for (const group of groups) {
        const chev = group.locator('.bowire-service-chevron').first();
        const cls = await chev.getAttribute('class').catch(() => '');
        if (!cls || !cls.includes('expanded')) {
            const header = group.locator('.bowire-service-header').first();
            await header.click().catch(() => {});
            await page.waitForTimeout(120);
        }
    }
    await page.waitForTimeout(400);
}

/** workspaces-switcher: open the topbar workspace switcher dropdown. */
async function captureWorkspacesSwitcher(page) {
    log('workspaces-switcher: navigate + seed multi-workspace state');
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'workspaces');
    // The workspace chip lives in the topbar's left cluster. Click it.
    const candidates = [
        '#bowire-workspace-switcher',
        '.bowire-workspace-switcher',
        '.bowire-topbar-workspace-chip',
        '.bowire-workspace-chip',
        '[data-bowire-workspace-switcher]'
    ];
    let opened = false;
    for (const sel of candidates) {
        const loc = page.locator(sel).first();
        if (await loc.isVisible().catch(() => false)) {
            await loc.click().catch(() => {});
            opened = true;
            break;
        }
    }
    if (!opened) {
        log('  no workspace switcher found; capturing topbar fallback');
    } else {
        await page.waitForTimeout(800);
    }
    await page.screenshot({
        path: outPath('workspaces-switcher'),
        clip: { x: 0, y: 0, width: 600, height: 500 }
    });
    log('workspaces-switcher: OK');
}

/** interceptor-flows: rail=interceptor. Tool standalone (5180) carries
 *  the package; Combined doesn't. */
async function captureInterceptorFlows(page) {
    log('interceptor-flows: navigate Tool + seed interceptor mode');
    await page.goto(TOOL, { waitUntil: 'domcontentloaded' });
    await seedAtRoot(page, TOOL + '/', 'interceptor');
    await page.waitForTimeout(1500);
    await page.screenshot({ path: outPath('interceptor-flows') });
    log('interceptor-flows: OK');
}

/** compose-rail: rail=compose with Library on the left. Tool ships Compose. */
async function captureComposeRail(page) {
    log('compose-rail: navigate Tool + seed compose');
    await page.goto(TOOL, { waitUntil: 'domcontentloaded' });
    const collection = {
        id: 'demo-coll',
        name: 'Harbor smoke',
        createdAt: 1_700_000_000_000,
        requests: [
            { id: 'r1', name: 'List docks',    method: 'GET',  url: '/api/docks',      body: '{}', headers: {} },
            { id: 'r2', name: 'List ships',    method: 'GET',  url: '/api/ships',      body: '{}', headers: {} },
            { id: 'r3', name: 'Schedule call', method: 'POST', url: '/api/port-calls', body: '{}', headers: {} }
        ]
    };
    await seedAtRoot(page, TOOL + '/', 'compose', { collections: [collection] });
    await page.waitForTimeout(1800);
    await page.screenshot({ path: outPath('compose-rail') });
    log('compose-rail: OK');
}

/** map-widget-pins: hit Sample.Embedded ListLocations, wait for pins. */
async function captureMapWidgetPins(page) {
    log('map-widget-pins: navigate Sample.Embedded');
    await page.goto(EMBEDDED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await page.evaluate(() => {
        try {
            const id = 'harbor';
            localStorage.setItem('bowire_workspaces', JSON.stringify([{
                id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000
            }]));
            localStorage.setItem('bowire_active_workspace_id', id);
            localStorage.setItem('bowire_ws_' + id + '_server_urls', JSON.stringify(['http://localhost:5181/']));
            localStorage.setItem('bowire_rail_mode', 'discover');
            localStorage.setItem('bowire_theme_pref', 'dark');
        } catch { /* ignore */ }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 25000 });
    await page.waitForTimeout(1500);
    await expandAllServices(page);
    let picked = page.locator('.bowire-method-item:has-text("Locations")').first();
    if (!(await picked.isVisible().catch(() => false))) {
        picked = page.locator('.bowire-method-item:has-text("locations")').first();
    }
    if (!(await picked.isVisible().catch(() => false))) {
        throw new Error('no Locations method visible');
    }
    await picked.click();
    await page.waitForTimeout(500);
    const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
    if (!(await exec.isEnabled().catch(() => false))) {
        throw new Error('execute button not enabled');
    }
    await exec.click();
    const respHit = await waitForAny(page, [
        '.bowire-response-output.is-interactive',
        '.bowire-json-tree'
    ], 10000);
    if (!respHit) throw new Error('no response after execute');
    const mapHit = await waitForAny(page, [
        '.bowire-map-widget', '.bowire-map', '.leaflet-container', '.maplibregl-canvas'
    ], 8000);
    if (mapHit) log('  map hit: ' + mapHit);
    else log('  map widget never mounted — capturing anyway');
    await page.waitForTimeout(1800);
    await page.screenshot({
        path: outPath('map-widget-pins'),
        clip: { x: 280, y: 110, width: 1100, height: 680 }
    });
    log('map-widget-pins: OK');
}

const surfaces = [
    { name: 'workspaces-switcher',  fn: captureWorkspacesSwitcher },
    { name: 'interceptor-flows',    fn: captureInterceptorFlows },
    { name: 'compose-rail',         fn: captureComposeRail },
    { name: 'map-widget-pins',      fn: captureMapWidgetPins }
];

(async () => {
    log('Launching Chromium…');
    const browser = await chromium.launch({
        headless: true,
        args: ['--ignore-certificate-errors']
    });
    const context = await browser.newContext({
        viewport: { width: WIDTH, height: HEIGHT },
        deviceScaleFactor: DSF,
        ignoreHTTPSErrors: true,
        colorScheme: 'dark'
    });
    const page = await context.newPage();
    await page.emulateMedia({ colorScheme: 'dark' });

    page.on('console', msg => {
        if (msg.type() === 'error') {
            const t = msg.text();
            if (t.length < 200) log(`[browser ERR] ${t}`);
        }
    });

    const results = [];
    for (const s of surfaces) {
        try {
            await s.fn(page);
            results.push({ name: s.name, ok: true });
        } catch (e) {
            log(`[FAIL ${s.name}] ${e.message}`);
            try { await page.screenshot({ path: outPath(s.name + '.fail-debug') }); } catch { /* ignore */ }
            results.push({ name: s.name, ok: false, err: e.message });
        }
    }

    await context.close();
    await browser.close();

    log('Summary:');
    for (const r of results) {
        log(`  ${r.ok ? 'OK  ' : 'FAIL'} ${r.name}${r.err ? ' — ' + r.err : ''}`);
    }
    const okCount = results.filter(r => r.ok).length;
    log(`${okCount}/${surfaces.length} surfaces captured.`);
    process.exit(okCount > 0 ? 0 : 1);
})().catch(e => {
    console.error('[fatal]', e && e.stack || e);
    process.exit(1);
});
