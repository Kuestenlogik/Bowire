/**
 * Captures the v2.1 surface set for the marketing site & docs.
 *
 * Each capture is a targeted clip into
 *     site/assets/images/screenshots/v2-1/<name>.png
 * — clips are used when a sub-region IS the story, full-pane shots when
 * the layout itself is what we want to show.
 *
 * Operator MUST already have these processes running:
 *   http://localhost:5180          Tool standalone (workbench at /)
 *   https://localhost:5101/bowire  Combined Harbor sample
 *   http://localhost:5181/bowire   Sample.Embedded (REST + Map)
 *   http://localhost:5182          Sample.TacticalApi (gRPC)
 *
 * If `require('@playwright/test')` throws we BAIL with a one-line
 * message — we don't try to install / repair Playwright in-band.
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

const OUT = path.resolve(__dirname, '..', '..', 'site', 'assets', 'images', 'screenshots', 'v2-1');
fs.mkdirSync(OUT, { recursive: true });

const TOOL = 'http://localhost:5180';
const COMBINED_BOWIRE = 'https://localhost:5101/bowire';
const COMBINED_ROOT = 'https://localhost:5101/';
const EMBEDDED_BOWIRE = 'http://localhost:5181/bowire';
const EMBEDDED_ROOT = 'http://localhost:5181/';
const TACTICAL_ROOT = 'http://localhost:5182/';

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

/**
 * Seed localStorage so the workbench boots into a workspace with the
 * given source URL bound + the rail in the requested mode. Same shape
 * record-demo.js uses; copied verbatim so this script stays self-contained.
 */
async function seed(page, sampleUrl, railMode) {
    railMode = railMode || 'discover';
    await page.evaluate(({ sampleUrl, railMode }) => {
        try {
            const id = 'harbor';
            localStorage.setItem('bowire_workspaces', JSON.stringify([{
                id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000
            }]));
            localStorage.setItem('bowire_active_workspace_id', id);
            localStorage.setItem('bowire_ws_' + id + '_server_urls', JSON.stringify([sampleUrl]));
            localStorage.setItem('bowire_rail_mode', railMode);
            localStorage.setItem('bowire_theme_pref', 'dark');
        } catch { /* ignore */ }
    }, { sampleUrl, railMode });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
}

/** Same as seed() but with a configurable ready-timeout. */
async function seedSlow(page, sampleUrl, railMode, readyTimeoutMs) {
    await page.evaluate(({ sampleUrl, railMode }) => {
        try {
            const id = 'harbor';
            localStorage.setItem('bowire_workspaces', JSON.stringify([{
                id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000
            }]));
            localStorage.setItem('bowire_active_workspace_id', id);
            localStorage.setItem('bowire_ws_' + id + '_server_urls', JSON.stringify([sampleUrl]));
            localStorage.setItem('bowire_rail_mode', railMode);
            localStorage.setItem('bowire_theme_pref', 'dark');
        } catch { /* ignore */ }
    }, { sampleUrl, railMode });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: readyTimeoutMs });
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

// ─── Capture helpers ───────────────────────────────────────────────

/** rail-strip: clip the activity rail on the Combined sample. */
async function captureRailStrip(page) {
    log('rail-strip: navigate + seed');
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover');
    // Wait for the rail to mount AND have its mode buttons painted.
    await page.waitForSelector('#bowire-activity-rail .bowire-rail-btn', { timeout: 15000 });
    await page.waitForTimeout(800);
    // Rail is 48px wide and sits flush at x=0, below the 56px topbar.
    // Capture down to 720px to show the full mode stack + the bottom
    // sidebar-toggle button without grabbing the empty bg below it.
    await page.screenshot({
        path: outPath('rail-strip'),
        clip: { x: 0, y: 56, width: 56, height: 720 }
    });
    log('rail-strip: OK');
}

/** discover-with-response: click a List method, hit Execute, capture
 *  whole workbench so the rail + sidebar + response card all show. */
async function captureDiscoverWithResponse(page) {
    log('discover-with-response: navigate + seed');
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover');
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 45000 });
    await page.waitForTimeout(1000);
    await expandAllServices(page);

    const list = page.locator('.bowire-method-item', { hasText: 'List' }).first();
    if (!(await list.isVisible().catch(() => false))) {
        throw new Error('no "List" method visible after expand');
    }
    await list.click();
    await page.waitForTimeout(600);
    const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
    if (!(await exec.isEnabled().catch(() => false))) {
        throw new Error('execute button not enabled');
    }
    await exec.click();
    const hit = await waitForAny(page, [
        '.bowire-response-output.is-interactive',
        '.bowire-response-output.error',
        '.bowire-json-tree',
        '.bowire-response-output:has(pre)'
    ], 10000);
    if (!hit) throw new Error('response never landed');
    await page.waitForTimeout(800);
    // Layout IS the story — full pane (the full viewport).
    await page.screenshot({ path: outPath('discover-with-response') });
    log('discover-with-response: OK');
}

/** compose-library-left: rail=compose. Library sidebar sits LEFT in v2.1. */
async function captureComposeLibraryLeft(page) {
    log('compose-library-left: navigate + seed compose');
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    // Pre-seed a collection so the library sidebar has content to show
    // — an empty workspace renders an empty-state hint instead of the
    // collection tree, which misses the layout-flip story.
    await page.evaluate(() => {
        try {
            const id = 'harbor';
            localStorage.setItem('bowire_workspaces', JSON.stringify([{
                id, name: 'Harbor demo', color: 'sky', createdAt: 1_700_000_000_000
            }]));
            localStorage.setItem('bowire_active_workspace_id', id);
            localStorage.setItem('bowire_ws_' + id + '_server_urls', JSON.stringify(['https://localhost:5101/']));
            localStorage.setItem('bowire_rail_mode', 'compose');
            localStorage.setItem('bowire_theme_pref', 'dark');
            // Seed a tiny collection with two requests so the library
            // tree has something to render. The shape mirrors what
            // the in-app save flow produces.
            const collection = {
                id: 'demo-coll',
                name: 'Harbor smoke',
                createdAt: 1_700_000_000_000,
                requests: [
                    { id: 'r1', name: 'List docks',     method: 'GET', url: '/api/docks',     body: '{}', headers: {} },
                    { id: 'r2', name: 'List ships',     method: 'GET', url: '/api/ships',     body: '{}', headers: {} },
                    { id: 'r3', name: 'Schedule call',  method: 'POST', url: '/api/port-calls', body: '{}', headers: {} }
                ]
            };
            localStorage.setItem('bowire_ws_' + id + '_collections', JSON.stringify([collection]));
        } catch { /* ignore */ }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
    await page.waitForTimeout(2000);
    await page.screenshot({ path: outPath('compose-library-left') });
    log('compose-library-left: OK');
}

/** map-pins: invoke /api/locations on Sample.Embedded, wait for the
 *  Map widget to mount + pins to land, clip to response+map split. */
async function captureMapPins(page) {
    log('map-pins: navigate Sample.Embedded directly');
    // Locations endpoint with coordinate{lat,lon} per item lives only
    // on Sample.Embedded (5181). Combined (5101) has no geo data. The
    // embedded UI bundle throws ReferenceError: loadFlows is not
    // defined during boot — we work around by waiting for method items
    // instead of the bowire-app-ready flag, which the boot error
    // prevents from ever firing.
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
    // Don't wait for bowire-app-ready (boot error blocks it). Wait
    // for the method-item list instead.
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 25000 });
    await page.waitForTimeout(1500);
    await expandAllServices(page);

    // Pick the ListLocations method.
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
    // Wait for the map widget — class name is .bowire-map-widget or
    // the leaflet container if the widget mounted leaflet.
    const mapHit = await waitForAny(page, [
        '.bowire-map-widget',
        '.bowire-map',
        '.leaflet-container',
        '.maplibregl-canvas'
    ], 8000);
    if (mapHit) log('  map hit: ' + mapHit);
    else log('  map widget never mounted — capturing anyway');
    await page.waitForTimeout(1800);
    // Skip rail + sidebar to focus on the response+map split.
    await page.screenshot({
        path: outPath('map-pins'),
        clip: { x: 280, y: 110, width: 1100, height: 680 }
    });
    log('map-pins: OK');
}

/** settings-plugins-protocols: open the Plugins → Protocols sub-page
 *  by seeding settingsTab in localStorage, then clicking the gear.
 *  Tool standalone (5180) is down — use Combined (5101). The settings
 *  modal looks identical regardless of host. */
async function captureSettingsProtocols(page) {
    log('settings-plugins-protocols: navigate Combined');
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover');
    // Try rail-bottom settings gear first (.bowire-rail-settings) then
    // topbar gear (#bowire-settings-btn).
    const gear = page.locator('.bowire-rail-settings, #bowire-settings-btn').first();
    if (!(await gear.isVisible().catch(() => false))) {
        throw new Error('settings gear not visible');
    }
    await gear.click();
    const modalHit = await waitForAny(page, ['.bowire-settings-modal'], 8000);
    if (!modalHit) throw new Error('settings modal never opened');
    await page.waitForTimeout(600);
    // openSettings() always lands on 'general'. Click the Plugins
    // parent node — its onClick sets settingsTab='configure-protocols'.
    const plugins = page.locator('.bowire-settings-modal').locator('text=Plugins').first();
    if (await plugins.isVisible().catch(() => false)) {
        await plugins.click();
        await page.waitForTimeout(600);
    } else {
        log('  Plugins tree node not found; capturing General fallback');
    }
    await page.screenshot({
        path: outPath('settings-plugins-protocols'),
        clip: { x: 100, y: 60, width: 1200, height: 800 }
    });
    log('settings-plugins-protocols: OK');
}

/** help-rail: rail=help. Wait for topic tree and a topic body before
 *  the full-pane shot. Tool standalone (5180) is down — use Combined. */
async function captureHelpRail(page) {
    log('help-rail: navigate Combined, seed help mode');
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'help');
    // Help-mode mounts a topic tree + a topic body pane. Wait for both.
    const treeHit = await waitForAny(page, [
        '.bowire-help-topic',
        '.bowire-help-tree',
        '.bowire-help-sidebar',
        '.bowire-help-nav'
    ], 8000);
    if (treeHit) log('  help tree hit: ' + treeHit);
    await page.waitForTimeout(1400);
    await page.screenshot({ path: outPath('help-rail') });
    log('help-rail: OK');
}

/** streaming-state-badge: gRPC subscribe stream. Clip the response-pane
 *  header showing the state pill + count badge after ≥3 frames. */
async function captureStreamingBadge(page) {
    log('streaming-state-badge: navigate combined (WatchCrane stream)');
    // Combined sample (5101) bundles a gRPC HarborService whose
    // WatchCrane method is a server-streaming RPC that ticks 1 Hz.
    // The tactical h2c sample at 5182 isn't reachable from a browser
    // (no TLS + no gRPC-Web shim), so we use WatchCrane on Combined
    // — same surface, same state-badge story.
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover');
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 45000 });
    await page.waitForTimeout(1000);
    await expandAllServices(page);

    // Pick WatchCrane — the gRPC server-streaming RPC on Combined.
    const candidates = ['WatchCrane', 'Subscribe', 'Watch'];
    let picked = null;
    for (const text of candidates) {
        const m = page.locator('.bowire-method-item', { hasText: text }).first();
        if (await m.isVisible().catch(() => false)) { picked = m; break; }
    }
    if (!picked) throw new Error('no streaming method visible');
    await picked.scrollIntoViewIfNeeded();
    await picked.click();
    await page.waitForTimeout(800);
    // WatchCrane wants a craneId input — record-demo.js feeds "1".
    const craneId = page.locator('input[data-field-key="craneId"]').first();
    if (await craneId.isVisible().catch(() => false)) {
        await craneId.fill('1').catch(() => {});
        await page.waitForTimeout(200);
    }
    const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
    if (!(await exec.isEnabled().catch(() => false))) {
        throw new Error('execute / subscribe button not enabled');
    }
    await exec.click();
    // Wait for first frame.
    const firstHit = await waitForAny(page, [
        '.bowire-stream-list-item',
        '.bowire-stream-list-pane .bowire-stream-list-item'
    ], 10000);
    if (!firstHit) throw new Error('no stream frames within 10s');
    // Wait for ≥3 frames so the count badge shows >=3.
    const deadline = Date.now() + 8000;
    while (Date.now() < deadline) {
        const count = await page.locator('.bowire-stream-list-item').count().catch(() => 0);
        if (count >= 3) break;
        await page.waitForTimeout(300);
    }
    await page.waitForTimeout(300);
    // Clip the response-pane header — top strip of the right pane.
    await page.screenshot({
        path: outPath('streaming-state-badge'),
        clip: { x: 720, y: 110, width: 700, height: 80 }
    });
    // Stop the stream cleanly so it doesn't keep ticking for next captures.
    const cancel = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancel.isVisible().catch(() => false)) {
        await cancel.click().catch(() => {});
    }
    log('streaming-state-badge: OK');
}

// ─── Driver ────────────────────────────────────────────────────────

const surfaces = [
    { name: 'rail-strip',                  fn: captureRailStrip },
    { name: 'discover-with-response',      fn: captureDiscoverWithResponse },
    { name: 'compose-library-left',        fn: captureComposeLibraryLeft },
    { name: 'map-pins',                    fn: captureMapPins },
    { name: 'settings-plugins-protocols',  fn: captureSettingsProtocols },
    { name: 'help-rail',                   fn: captureHelpRail },
    { name: 'streaming-state-badge',       fn: captureStreamingBadge }
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
            // Ignore noisy Browser-error from leaflet tile 404s etc.
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
            // Save a debug shot to help diagnose.
            try {
                await page.screenshot({ path: outPath(s.name + '.fail-debug') });
            } catch { /* ignore */ }
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
