/**
 * Captures marketing screenshots by driving a single Bowire page with Playwright.
 *
 * Uses one shared page + navigates between states — opening a new page for each
 * shot triggers a fresh Service-Discovery round that can time out when fired
 * back-to-back.
 *
 * Assumes Kuestenlogik.Bowire.Samples.Combined is running on https://localhost:5101.
 * The Combined sample ships gRPC (HarborService), REST (PortCalls/Docks/Manifests)
 * and SignalR (PortCallHub) against a shared harbor domain — so a single Bowire
 * page can shoot every UI state without restarting.
 *
 * Writes 1280×720 PNGs to site/assets/images/screenshots/.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
const URL = 'https://localhost:5101/bowire';

// Make sure both target dirs exist. The docs/ mirror lets the Jekyll
// marketing features.html and the docfx features/*.md reference the
// same captures without duplicating the shoot.
for (const dir of [OUT, DOCS_OUT]) {
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
}

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

// Theme is set by the runner via the THEME env var; we capture into
// `name-${theme}.png` so the site can serve dark/light variants via
// CSS [data-theme] swap. The legacy `name.png` (theme-less) is kept
// as a copy of the dark variant for any markup that hasn't been
// migrated yet.
const THEME = (process.env.THEME || 'dark').toLowerCase();

async function shot(page, name) {
    const file = path.join(OUT, `${name}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${name}-${THEME}.png`));
    if (THEME === 'dark') {
        // Default fallback for any <img> not yet upgraded to the
        // theme-aware double-image pattern.
        fs.copyFileSync(file, path.join(OUT, `${name}.png`));
        fs.copyFileSync(file, path.join(DOCS_OUT, `${name}.png`));
    }
    log(`  -> ${name}-${THEME}.png  (site + docs)`);
}

// Empty-state captures land in docs/images/ as bowire-NAME.png (without
// the screenshots/ subdir) — referenced from docs/features/empty-state.md
// and docs/setup/standalone.md.
const QUICKSTART_OUT = path.resolve(__dirname, '..', 'docs', 'images');
async function shotQuickstart(page, name) {
    const file = path.join(QUICKSTART_OUT, `bowire-${name}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    // The Jekyll site's screenshot carousel references these images
    // without the `bowire-` prefix (e.g. ready-dark.png), so mirror
    // each capture into site/assets/images/screenshots/ too.
    fs.copyFileSync(file, path.join(OUT, `${name}-${THEME}.png`));
    if (THEME === 'dark') {
        fs.copyFileSync(file, path.join(QUICKSTART_OUT, `bowire-${name}.png`));
        fs.copyFileSync(file, path.join(OUT, `${name}.png`));
    }
    log(`  -> bowire-${name}-${THEME}.png  (docs + screenshots mirror)`);
}

async function resetToSidebar(page) {
    // Click through any open modal overlays.
    const closeButtons = await page.locator('.bowire-settings-close, .bowire-env-modal-close').all();
    for (const b of closeButtons) {
        if (await b.isVisible().catch(() => false)) await b.click().catch(() => {});
    }
    // Switch back to Services view pill so Sidebar shows all methods.
    const servicesPill = page.locator('#bowire-view-pill-services').first();
    if (await servicesPill.isVisible().catch(() => false)) {
        await servicesPill.click().catch(() => {});
    }
    await page.waitForTimeout(300);
    // Re-expand any service groups that re-collapsed on the view switch
    // — otherwise downstream `.click()` calls on method-items hit hidden
    // elements and time out.
    for (const g of await page.locator('.bowire-service-group.collapsed .bowire-service-group-header').all()) {
        await g.click().catch(() => {});
    }
    await page.waitForTimeout(300);
}

(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        ignoreHTTPSErrors: true,
        // Bowire's Auto theme mode follows prefers-color-scheme; emulate
        // the OS pref so the auto-fallback path lands on the same theme
        // as the explicit bowire_theme_pref localStorage value.
        colorScheme: THEME
    });
    const page = await ctx.newPage();
    await page.emulateMedia({ colorScheme: THEME });

    log('Opening Bowire…');
    // Force dark theme up front so every screenshot matches the site's
    // default palette. The localStorage key has to be set before the
    // first paint or the app briefly flashes light, so we land on
    // bowire/, set the pref, then reload.
    await page.goto(URL, { waitUntil: 'domcontentloaded' });
    await page.evaluate((t) => {
        try { localStorage.setItem('bowire_theme_pref', t); } catch {}
    }, THEME);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
    // Items render inside collapsed groups, so wait for DOM attachment
    // rather than visibility — the expand-loop below makes them visible.
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 30000 });
    // Expand every collapsed service group once.
    for (const g of await page.locator('.bowire-service-group.collapsed .bowire-service-group-header').all()) {
        await g.click().catch(() => {});
    }
    await page.waitForTimeout(500);

    // ---- 1) flow-editor ----
    log('flow-editor…');
    await page.locator('#bowire-view-pill-flows').click();
    await page.waitForTimeout(500);
    const createFlow = page.locator('#bowire-flow-create-btn, #bowire-flow-create-first-btn').first();
    if (await createFlow.isVisible().catch(() => false)) {
        await createFlow.click();
        await page.waitForTimeout(500);
    }
    for (const cls of ['bowire-flow-add-btn-blue', 'bowire-flow-add-btn-orange', 'bowire-flow-add-btn-gray']) {
        const btn = page.locator(`.${cls}`).first();
        if (await btn.isVisible().catch(() => false)) { await btn.click(); await page.waitForTimeout(200); }
    }
    await page.waitForTimeout(400);
    await shot(page, 'flow-editor');
    await resetToSidebar(page);

    // ---- 2) json-chaining ----
    // The first REST `List` method maps to GET /api/port-calls — a
    // plain unary GET that returns a list of port-call objects with
    // nested fields, perfect for the "click any JSON value to chain"
    // demo. (The methods named GetPort-calls / GetShip-tracker in
    // the sidebar are SSE streams that never finish, so they hang
    // until the HttpClient timeout.)
    try {
        log('json-chaining…');
        await page.locator('.bowire-method-item', { hasText: 'List' }).first().click();
        await page.waitForTimeout(1200);
        const execBtn = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
        await execBtn.waitFor({ state: 'visible', timeout: 10000 });
        await page.waitForFunction(
            () => {
                const b = document.querySelector('.bowire-execute-btn, #bowire-execute-btn');
                return b && !b.disabled;
            },
            { timeout: 10000 }
        );
        await execBtn.click();
        // Wait for the response pane to render — that's when the
        // "Executing..." spinner disappears and the JSON tree (with
        // .bowire-json-pickable spans) is laid out. Without this
        // condition the screenshot races the request and lands while
        // the loading spinner is still up.
        await page.waitForFunction(() => {
            const stillLoading = !!document.querySelector('.bowire-loading .bowire-loading-text');
            const haveJson = !!document.querySelector('.bowire-json-pickable');
            return !stillLoading && haveJson;
        }, { timeout: 20000 }).catch(() => {});
        // Tiny breath so the fade-in completes before the shot.
        await page.waitForTimeout(500);
        const pickable = page.locator('.bowire-json-pickable').first();
        if (await pickable.isVisible().catch(() => false)) {
            await pickable.hover();
            await page.waitForTimeout(400);
        }
        await shot(page, 'json-chaining');
    } catch (e) {
        log(`  (json-chaining failed: ${e.message.split('\n')[0]})`);
    }
    await resetToSidebar(page);

    // ---- 3) streaming ----
    // WatchCrane is a gRPC server-streaming RPC — ticks come in every
    // second from the sample's background timer.
    log('streaming…');
    await page.locator('.bowire-method-item', { hasText: 'WatchCrane' }).first().click();
    await page.waitForTimeout(800);
    await page.locator('input[data-field-key="craneId"]').first().fill('1');
    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    await page.waitForTimeout(2000);
    await shot(page, 'streaming');
    // Stop the stream so the next shot isn't racing against incoming frames.
    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});
    await page.waitForTimeout(800);
    await resetToSidebar(page);
    await page.waitForTimeout(500);
    await resetToSidebar(page);

    // ---- 4) command-palette ----
    try {
        log('command-palette…');
        await page.locator('#bowire-command-palette-input').click();
        await page.keyboard.type('port', { delay: 40 });
        await page.waitForTimeout(500);
        await shot(page, 'command-palette');
        await page.keyboard.press('Escape');
        await page.waitForTimeout(200);
    } catch (e) {
        log(`  (command-palette failed: ${e.message.split('\n')[0]})`);
    }

    // ---- 5) env-diff (environments view) ----
    try {
        log('env-diff…');
        await page.locator('#bowire-view-pill-environments').click();
        await page.waitForTimeout(600);
        await shot(page, 'env-diff');
        await resetToSidebar(page);
    } catch (e) {
        log(`  (env-diff failed: ${e.message.split('\n')[0]})`);
    }

    // ---- 6) settings ----
    try {
        log('settings…');
        await page.locator('#bowire-settings-btn').click();
        await page.waitForTimeout(700);
        await shot(page, 'settings');
        const settingsClose = page.locator('#bowire-settings-close-btn, .bowire-settings-close').first();
        if (await settingsClose.isVisible().catch(() => false)) await settingsClose.click();
        await page.waitForTimeout(300);
    } catch (e) {
        log(`  (settings failed: ${e.message.split('\n')[0]})`);
    }

    // ---- 7) recording ----
  try {
    log('recording…');
    // Start a recording, execute two unary methods, then open the manager.
    // GetPortCall (SignalR) + SchedulePortCall (gRPC unary) both come
    // back immediately so the shot lands on a populated recording table.
    const recordBtn = page.locator('#bowire-record-btn, .bowire-record-btn').first();
    if (await recordBtn.isVisible().catch(() => false)) {
        await recordBtn.click();
        await page.waitForTimeout(500);

        await page.locator('.bowire-method-item', { hasText: 'GetPortCall' }).first().click();
        await page.waitForTimeout(600);
        const recId = page.locator('input[data-field-key="portCallId"], input[data-field-key="id"]').first();
        if (await recId.isVisible().catch(() => false)) await recId.fill('1');
        await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
        await page.waitForTimeout(1200);

        await page.locator('.bowire-method-item', { hasText: 'SchedulePortCall' }).first().click();
        await page.waitForTimeout(600);
        const shipId = page.locator('input[data-field-key="shipId"]').first();
        if (await shipId.isVisible().catch(() => false)) await shipId.fill('1');
        const dockNum = page.locator('input[data-field-key="dockNumber"]').first();
        if (await dockNum.isVisible().catch(() => false)) await dockNum.fill('1');
        const arrivalTs = page.locator('input[data-field-key="scheduledArrivalUnixS"]').first();
        if (await arrivalTs.isVisible().catch(() => false)) {
            await arrivalTs.fill(String(Math.floor(Date.now() / 1000) + 3600));
        }
        await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
        await page.waitForTimeout(1200);

        await recordBtn.click(); // stop
        await page.waitForTimeout(400);
    }
    // Open the recording manager so the captured session shows up.
    const recordingsBtn = page.locator('#bowire-recordings-btn, [title*="ecording"]').first();
    if (await recordingsBtn.isVisible().catch(() => false)) {
        await recordingsBtn.click();
        await page.waitForTimeout(600);
        await shot(page, 'recording');
        const recClose = page.locator('.bowire-env-modal-close').first();
        if (await recClose.isVisible().catch(() => false)) await recClose.click();
        await page.waitForTimeout(300);
    } else {
        log('  (recordings manager button not found — leaving placeholder)');
    }
  } catch (e) {
    log(`  (recording failed: ${e.message.split('\n')[0]})`);
  }

    // ---- 8) performance ----
  try {
    log('performance…');
    // Make sure any overlay from the previous step (recording manager,
    // settings dialog, env modal) is dismissed before we drive the
    // method sidebar again — otherwise the UI is behind a modal and
    // input fields report disabled / not-visible.
    await page.keyboard.press('Escape').catch(() => {});
    await page.waitForTimeout(200);
    await page.keyboard.press('Escape').catch(() => {});
    await resetToSidebar(page);
    await page.waitForTimeout(400);

    // Pick a unary method and open the Performance tab in the response
    // pane. GetPortCall (SignalR) is a safe unary pick — 50 round-trips
    // fill the latency chart without mutating state.
    await page.locator('.bowire-method-item', { hasText: 'GetPortCall' }).first().click();
    await page.waitForTimeout(600);
    const perfId = page.locator('input[data-field-key="portCallId"], input[data-field-key="id"]').first();
    if (await perfId.isVisible().catch(() => false)) await perfId.fill('1');

    // Switch to Performance tab (only appears for unary methods).
    const perfTab = page.locator('#bowire-response-tab-performance').first();
    if (await perfTab.isVisible().catch(() => false)) {
        await perfTab.click();
        await page.waitForTimeout(400);

        // Short run so the shot captures a populated chart without
        // waiting seconds for 1000 calls to finish. The inputs are
        // marked disabled while `benchmark.running` is true — pass
        // { force: true } so stale state from a prior run doesn't
        // wedge the capture; runBenchmark reads the values fresh.
        const callsInput = page.locator('#bowire-perf-calls-input').first();
        if (await callsInput.isVisible().catch(() => false)) {
            await callsInput.fill('50', { force: true }).catch(() => {});
        }
        const concInput = page.locator('#bowire-perf-concurrency-input').first();
        if (await concInput.isVisible().catch(() => false)) {
            await concInput.fill('4', { force: true }).catch(() => {});
        }

        const runBtn = page.locator('#bowire-perf-run-btn').first();
        if (await runBtn.isVisible().catch(() => false)) {
            await runBtn.click();
            // Wait for the benchmark to finish. 50 calls x ~20ms each
            // at concurrency 4 ≈ 250ms; give it more headroom.
            await page.waitForTimeout(3000);
            await shot(page, 'performance');
        } else {
            log('  (perf run button not found — leaving placeholder)');
        }
    } else {
        log('  (Performance tab not found — method probably non-Unary)');
    }
  } catch (e) {
    log(`  (performance failed: ${e.message.split('\n')[0]})`);
  }

    // ---- 9) schema-upload ----
  try {
    // This state needs the first-run landing. We simulate it by wiping the
    // server URL list in localStorage and reloading — that drops the page
    // back into the "no URL" onboarding screen.
    log('schema-upload…');
    await page.evaluate(() => {
        try {
            localStorage.removeItem('bowire_server_urls');
            localStorage.setItem('bowire_server_urls', '[]');
        } catch {}
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15000 });
    await page.waitForTimeout(800);
    await shot(page, 'schema-upload');
    await shotQuickstart(page, 'first-run');
  } catch (e) {
    log(`  (schema-upload failed: ${e.message.split('\n')[0]})`);
  }

    // ---- 10) bowire-* quickstart shots — empty / error / discover states ----
    // After schema-upload we're on the first-run landing. Drive a few
    // edge cases so the docs/setup + features pages match the new
    // wordmark. Each capture is best-effort; any failure is logged and
    // skipped without aborting later captures.
    // Helper — wait until the loading spinner has gone away (either
    // services discovered or discovery failed). Connect-refused on a
    // dead port can take 15+ seconds, so generous timeout.
    const waitForDiscoveryDone = async () => {
        await page.waitForFunction(() => {
            const el = document.querySelector('.bowire-loading-services, [class*="loading-services"]');
            const txt = (document.body && document.body.innerText) || '';
            const stillLoading = !!el || /Discovering services/i.test(txt);
            return !stillLoading;
        }, { timeout: 30000 }).catch(() => {});
    };

    // Wipe every Bowire localStorage / IndexedDB / sessionStorage key
    // before each discovery-state shot so the prior run's cached
    // services / favourites / environments don't bleed into the next
    // capture. Without this, switching to an invalid URL still shows
    // the previous run's cached method list, which makes the "failed"
    // and "multi-url" shots look indistinguishable from "ready".
    const wipeAndSetUrls = async (urls) => {
        // Nuke EVERY storage layer that could persist a prior discovery's
        // method list (the app caches discovered services in IndexedDB
        // alongside localStorage, so a bowire_* prefix wipe alone leaves
        // the sidebar populated and the "failed" shot looks identical to
        // "ready"). Then set the target URL list and reload from a
        // genuinely empty cache.
        await ctx.clearCookies().catch(() => {});
        await page.evaluate(async ({ urlsJson, theme }) => {
            try { localStorage.clear(); } catch {}
            try { sessionStorage.clear(); } catch {}
            try {
                if (window.indexedDB && indexedDB.databases) {
                    const dbs = await indexedDB.databases();
                    await Promise.all(dbs.map(d => new Promise(res => {
                        const req = indexedDB.deleteDatabase(d.name);
                        req.onsuccess = req.onerror = req.onblocked = () => res();
                    })));
                }
            } catch {}
            try {
                localStorage.setItem('bowire_server_urls', urlsJson);
                // Restore the theme pref that the screenshots rely on —
                // localStorage.clear() above wiped it.
                localStorage.setItem('bowire_theme_pref', theme);
            } catch {}
        }, { urlsJson: JSON.stringify(urls), theme: THEME });
    };

    try {
        log('quickstart: discovery-failed (may take ~15s)…');
        await wipeAndSetUrls(['https://localhost:65535']);
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15000 });
        await waitForDiscoveryDone();
        await page.waitForTimeout(1000);
        await shotQuickstart(page, 'discovery-failed');
    } catch (e) {
        log(`  (discovery-failed failed: ${e.message.split('\n')[0]})`);
    }
    try {
        log('quickstart: multi-url (may take ~15s)…');
        await wipeAndSetUrls(['https://localhost:5101', 'https://localhost:65535']);
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15000 });
        await waitForDiscoveryDone();
        await page.waitForTimeout(1000);
        await shotQuickstart(page, 'multi-url');
    } catch (e) {
        log(`  (multi-url failed: ${e.message.split('\n')[0]})`);
    }
    try {
        log('quickstart: ready (back to single working URL)…');
        await page.evaluate(() => {
            try {
                localStorage.setItem('bowire_server_urls', JSON.stringify(['https://localhost:5101']));
            } catch {}
        });
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15000 });
        await page.waitForSelector('.bowire-method-item', { timeout: 30000 });
        await page.waitForTimeout(800);
        await shotQuickstart(page, 'ready');

        // method-detail: pick a method, full pane visible
        const m = page.locator('.bowire-method-item').first();
        if (await m.isVisible().catch(() => false)) {
            await m.click();
            await page.waitForTimeout(800);
            await shotQuickstart(page, 'method-detail');
        }

        // editable: empty edit state — open a fresh "ad-hoc" request
        const adhoc = page.locator('#bowire-adhoc-btn, [data-action="adhoc"]').first();
        if (await adhoc.isVisible().catch(() => false)) {
            await adhoc.click();
            await page.waitForTimeout(500);
            await shotQuickstart(page, 'editable');
        } else {
            // fallback: capture the same ready frame as a Bowire-branded
            // placeholder for the editable state.
            await shotQuickstart(page, 'editable');
        }

        // wrong-protocol: type a search query that filters everything
        // out so the empty-state lands.
        const search = page.locator('#bowire-sidebar-search-input, #bowire-method-filter').first();
        if (await search.isVisible().catch(() => false)) {
            await search.fill('zzzzzz');
            await page.waitForTimeout(500);
            await shotQuickstart(page, 'wrong-protocol');
            await search.fill('');
        } else {
            await shotQuickstart(page, 'wrong-protocol');
        }
    } catch (e) {
        log(`  (ready/method-detail/editable/wrong-protocol failed: ${e.message.split('\n')[0]})`);
    }

    await ctx.close();
    await browser.close();
    log('done');
})();
