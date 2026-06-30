/**
 * Refresh Bowire's highest-impact marquee screenshot assets for a release.
 *
 * Distinct lifecycle from capture-v2-1.js: marquee assets refresh per
 * release (this script), v2-1/* are landing-page surfaces tied to that
 * release's narrative. We keep them separate so a v2.1 -> v2.2 refresh
 * doesn't churn v2-1/.
 *
 * Targets (under site/assets/images/screenshots/):
 *   ready.png                  Single most-seen Bowire shot — populated
 *                              workbench, rail strip visible, Discover
 *                              active, response card rendered.
 *   streaming-grpc-dark.png    gRPC streaming subscription with state
 *   streaming-grpc-light.png   badge + count pill + accumulating frames.
 *
 * Skipped (server not reachable from this worktree):
 *   streaming-akka-{dark,light}.png
 *   streaming-dis-{dark,light}.png
 *   streaming-graphql-{dark,light}.png
 *   streaming-kafka-{dark,light}.png
 *   streaming-mqtt-{dark,light}.png
 * Those sample servers live in the Bowire.Samples sibling repo. Run
 * this script there (or wire the samples up locally) to refresh them.
 *
 * Operator MUST already have these processes running:
 *   http://localhost:5180          Tool standalone (workbench at /)
 *   https://localhost:5101/bowire  Combined Harbor sample
 *   http://localhost:5181/bowire   Sample.Embedded (REST + Map)
 *   http://localhost:5182          Sample.TacticalApi (gRPC h2c)
 *
 * If the Tool standalone (5180) is down we fall back to the Combined
 * sample for the ready.png shot — same workbench surface, same v2.1
 * visual signals — and log the substitution.
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

// TWO `..` hops from __dirname — same fix that landed in record-demo /
// build-activity-snapshot / build-docs-pdf around 9fa1a1c.
const OUT = path.resolve(__dirname, '..', '..', 'site', 'assets', 'images', 'screenshots');
fs.mkdirSync(OUT, { recursive: true });

const TOOL = 'http://localhost:5180';
const COMBINED_BOWIRE = 'https://localhost:5101/bowire';
const COMBINED_ROOT = 'https://localhost:5101/';

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

/** Seed localStorage + reload so the workbench boots into a workspace
 *  with the given source URL bound, rail mode set, and theme picked. */
async function seed(page, sampleUrl, railMode, theme) {
    railMode = railMode || 'discover';
    theme = theme || 'dark';
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
    // Don't hard-fail on bowire-app-ready — Sample.Embedded has a known
    // boot-error path that blocks it. Method-item attachment is the
    // reliable signal that discovery finished.
    await page.waitForSelector('#bowire-app', { timeout: 20000 });
    await page.waitForTimeout(400);
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

// ─── Captures ──────────────────────────────────────────────────────

/** ready.png — populated workbench with all v2.1 visual signals.
 *  Rail strip left, Discover active, services in sidebar, one method
 *  selected, a meaningful response card rendered. Full-pane. */
async function captureReady(page, host, theme) {
    log(`ready: navigating ${host}, theme=${theme}`);
    const bowirePath = host === TOOL ? '/' : '/bowire';
    await page.goto(host + bowirePath, { waitUntil: 'domcontentloaded' });
    // The Tool standalone has no /bowire prefix — the workbench is at /.
    // Sample URL is always the combined Harbor sample (richer surface).
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 45000 });
    await page.waitForTimeout(1000);
    await expandAllServices(page);

    // Prefer a GET / List style method that returns a meaningful JSON
    // payload. Try a small ranked list of candidates.
    const candidates = ['List', 'GetShipTracker', 'GetShips', 'GetDocks', 'GetHarbor', 'Get'];
    let picked = null;
    for (const text of candidates) {
        const m = page.locator('.bowire-method-item', { hasText: text }).first();
        if (await m.isVisible().catch(() => false)) { picked = m; break; }
    }
    if (!picked) {
        // Fall back: first visible method item, whatever it is.
        picked = page.locator('.bowire-method-item').first();
    }
    if (!(await picked.isVisible().catch(() => false))) {
        throw new Error('no method item visible to click');
    }
    await picked.click();
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
    await page.waitForTimeout(900);
    await page.screenshot({ path: outPath('ready') });
    log('ready: OK');
}

/** streaming-grpc-{theme}.png — gRPC server-streaming subscription with
 *  state badge + count pill + accumulating frames. Combined sample's
 *  HarborService.WatchCrane is a 1 Hz server stream. */
async function captureStreamingGrpc(page, theme) {
    log(`streaming-grpc-${theme}: navigate Combined`);
    await page.goto(COMBINED_BOWIRE, { waitUntil: 'domcontentloaded' });
    await seed(page, COMBINED_ROOT, 'discover', theme);
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 45000 });
    await page.waitForTimeout(1000);
    await expandAllServices(page);

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

    // WatchCrane takes a craneId — record-demo feeds "1".
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
    const firstHit = await waitForAny(page, [
        '.bowire-stream-list-item',
        '.bowire-stream-list-pane .bowire-stream-list-item'
    ], 12000);
    if (!firstHit) throw new Error('no stream frames within 12s');
    // Wait for ≥3 frames so the count badge shows a meaningful number.
    const deadline = Date.now() + 9000;
    while (Date.now() < deadline) {
        const count = await page.locator('.bowire-stream-list-item').count().catch(() => 0);
        if (count >= 3) break;
        await page.waitForTimeout(300);
    }
    await page.waitForTimeout(500);

    // Wide clip on the response pane so the badge + count + stream rows
    // are all in frame, but the sidebar + rail context still reads. The
    // rail is 48px wide + sidebar ~260px — start clip slightly inside
    // the rail so the active mode dot is in shot.
    await page.screenshot({
        path: outPath('streaming-grpc-' + theme),
        clip: { x: 0, y: 0, width: WIDTH, height: HEIGHT }
    });

    // Stop the stream so it doesn't keep ticking + skewing the next shot.
    const cancel = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancel.isVisible().catch(() => false)) {
        await cancel.click().catch(() => {});
        await page.waitForTimeout(300);
    }
    log(`streaming-grpc-${theme}: OK`);
}

// ─── Driver ────────────────────────────────────────────────────────

(async () => {
    log('Launching Chromium…');
    const browser = await chromium.launch({
        headless: true,
        args: ['--ignore-certificate-errors']
    });
    const results = [];

    // 1) ready.png — capture against the Combined sample because the
    //    workbench mounted there can discover its own origin (CORS-free
    //    + same self-signed cert chain). The Tool standalone at :5180
    //    runs the same workbench surface but can't discover :5101
    //    cross-origin without browser-side CORS configuration, so it
    //    paints the empty-state instead. Same v2.1 visual signals
    //    either way — rail strip, sidebar, response card.
    {
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
                if (t.length < 200) log(`[browser ERR ready] ${t}`);
            }
        });
        try {
            await captureReady(page, 'https://localhost:5101', 'dark');
            results.push({ name: 'ready.png', ok: true, host: COMBINED_BOWIRE });
        } catch (e) {
            log(`[FAIL ready] ${e.message}`);
            try { await page.screenshot({ path: outPath('ready.fail-debug') }); } catch {}
            results.push({ name: 'ready.png', ok: false, err: e.message });
        }
        await context.close();
    }

    // 2) streaming-grpc-{dark,light}.png — fresh context per theme so
    //    the colorScheme + emulateMedia take effect cleanly.
    for (const theme of ['dark', 'light']) {
        const context = await browser.newContext({
            viewport: { width: WIDTH, height: HEIGHT },
            deviceScaleFactor: DSF,
            ignoreHTTPSErrors: true,
            colorScheme: theme
        });
        const page = await context.newPage();
        await page.emulateMedia({ colorScheme: theme });
        page.on('console', msg => {
            if (msg.type() === 'error') {
                const t = msg.text();
                if (t.length < 200) log(`[browser ERR streaming-${theme}] ${t}`);
            }
        });
        try {
            await captureStreamingGrpc(page, theme);
            results.push({ name: `streaming-grpc-${theme}.png`, ok: true });
        } catch (e) {
            log(`[FAIL streaming-grpc-${theme}] ${e.message}`);
            try { await page.screenshot({ path: outPath(`streaming-grpc-${theme}.fail-debug`) }); } catch {}
            results.push({ name: `streaming-grpc-${theme}.png`, ok: false, err: e.message });
        }
        await context.close();
    }

    // 3) Skipped protocols — logged for the report.
    const skipped = ['akka', 'dis', 'graphql', 'kafka', 'mqtt'];
    for (const proto of skipped) {
        results.push({
            name: `streaming-${proto}-{dark,light}.png`,
            ok: false,
            skipped: true,
            err: 'server not reachable from this worktree (Bowire.Samples sibling not running)'
        });
    }

    await browser.close();

    log('Summary:');
    for (const r of results) {
        if (r.skipped) {
            log(`  SKIP ${r.name} — ${r.err}`);
        } else {
            log(`  ${r.ok ? 'OK  ' : 'FAIL'} ${r.name}${r.host ? ' [' + r.host + ']' : ''}${r.err ? ' — ' + r.err : ''}`);
        }
    }
    const okCount = results.filter(r => r.ok).length;
    const failCount = results.filter(r => !r.ok && !r.skipped).length;
    log(`${okCount} captured, ${failCount} failed, ${results.filter(r => r.skipped).length} skipped.`);
    process.exit(okCount > 0 ? 0 : 1);
})().catch(e => {
    console.error('[fatal]', e && e.stack || e);
    process.exit(1);
});
