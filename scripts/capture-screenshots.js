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

/**
 * Capture a marketing screenshot.
 *
 * Opts:
 *   selector  — CSS selector to crop to (page.locator(sel).screenshot()).
 *               Without it, the whole viewport gets captured. Marketing
 *               scenes that demo a specific feature (env-diff, settings
 *               dialog, recording detail) crop to the relevant pane so
 *               the asset only shows what the surrounding copy claims it
 *               does, not the entire workbench chrome.
 *   padding   — extra px padding around the cropped element (default 0).
 *               Useful when the element's own border-radius / shadow
 *               needs breathing room.
 */
async function shot(page, name, opts) {
    opts = opts || {};
    const file = path.join(OUT, `${name}-${THEME}.png`);
    if (opts.selector) {
        const target = page.locator(opts.selector).first();
        await target.waitFor({ state: 'visible', timeout: 5000 });
        if (opts.padding) {
            // Pad: capture page-level with explicit clip taken from the
            // bounding box so we keep a controllable margin around the
            // target without altering layout.
            const box = await target.boundingBox();
            if (box) {
                const p = opts.padding;
                await page.screenshot({
                    path: file,
                    clip: {
                        x: Math.max(0, box.x - p),
                        y: Math.max(0, box.y - p),
                        width: box.width + p * 2,
                        height: box.height + p * 2
                    }
                });
            } else {
                await target.screenshot({ path: file });
            }
        } else {
            await target.screenshot({ path: file });
        }
    } else {
        await page.screenshot({ path: file, fullPage: false });
    }
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

/**
 * v2.0: rail-mode catalogue replaces the old #bowire-view-pill-* row.
 * Every mode-switch icon is a .bowire-rail-btn carrying data-rail-mode-id.
 * Use this helper instead of legacy `#bowire-view-pill-<view>` IDs.
 */
async function clickRail(page, modeId) {
    const sel = `.bowire-rail-btn[data-rail-mode-id="${modeId}"]`;
    const btn = page.locator(sel).first();
    if (await btn.isVisible().catch(() => false)) {
        await btn.click().catch(() => {});
        await page.waitForTimeout(300);
        return true;
    }
    return false;
}

async function resetToSidebar(page) {
    // Close any open right-side drawer (Assistant / Help / Tests /
    // Activity all share the unified .bowire-drawer-close chrome).
    const closeButtons = await page.locator('.bowire-drawer-close').all();
    for (const b of closeButtons) {
        if (await b.isVisible().catch(() => false)) await b.click().catch(() => {});
    }
    // Close the App-Drawer if open (B-Logo burger). Esc handles both.
    await page.keyboard.press('Escape').catch(() => {});
    await page.waitForTimeout(200);
    // Switch back to Discover rail so the sidebar shows the services tree.
    await clickRail(page, 'discover');
    await expandAllGroups(page);
}

// Click every collapsed-group header. Idempotent — running it twice in
// a row is a no-op for groups that are already expanded.
async function expandAllGroups(page) {
    for (const g of await page.locator('.bowire-service-group.collapsed .bowire-service-group-header').all()) {
        await g.click().catch(() => {});
    }
    await page.waitForTimeout(300);
}

// Click a method-item by name, robust against the post-view-switch
// race where the item is DOM-attached but hidden behind a
// `display: none` group ancestor or scrolled out of the sidebar
// viewport. Three escalating strategies:
//   1. Re-expand groups + scroll into view + normal Playwright click.
//   2. Playwright force-click (bypasses actionability checks).
//   3. Direct DOM .click() via page.evaluate — bypasses Playwright's
//      visibility wall entirely. Bowire's sidebar wires its handler
//      with addEventListener('click'), so the synthetic event fires
//      the same way a user click would.
async function clickMethodItem(page, text) {
    await expandAllGroups(page);
    const item = page.locator('.bowire-method-item', { hasText: text }).first();
    await item.waitFor({ state: 'attached', timeout: 10000 });
    try { await item.scrollIntoViewIfNeeded({ timeout: 5000 }); } catch { /* fine */ }
    try {
        await item.click({ timeout: 8000 });
        return;
    } catch { /* fall through to force */ }
    try {
        await item.click({ force: true, timeout: 5000 });
        return;
    } catch { /* fall through to JS click */ }
    // Last resort: synthetic DOM click. Items render their text inside
    // a child span, so we walk every method-item and substring-match
    // on textContent.
    const clicked = await page.evaluate((needle) => {
        const items = Array.from(document.querySelectorAll('.bowire-method-item'));
        const target = items.find(i => (i.textContent || '').includes(needle));
        if (!target) return false;
        target.click();
        return true;
    }, text);
    if (!clicked) {
        throw new Error(`clickMethodItem: no .bowire-method-item matched ${JSON.stringify(text)}`);
    }
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
        try {
            localStorage.setItem('bowire_theme_pref', t);
            // v2.0 boots into Home rail by default; the Discover
            // services tree only attaches to the DOM when Discover is
            // active. Seeding `bowire_rail_mode` before the reload
            // makes the page boot directly into Discover without a
            // post-load click that races with any auto-restore.
            localStorage.setItem('bowire_rail_mode', 'discover');
            // The renderMain force-home rule (render-main.js: when no
            // active workspace AND workspaces.length === 0) overwrites
            // any pre-seeded rail mode back to 'home' during the first
            // paint. Seed a default workspace so that guard passes and
            // the Discover sidebar actually renders.
            var wsId = 'ws_capture';
            localStorage.setItem('bowire_workspaces', JSON.stringify([{
                id: wsId,
                name: 'Capture',
                color: '#6366f1',
                createdAt: Date.now(),
                lastOpenedAt: Date.now()
            }]));
            localStorage.setItem('bowire_active_workspace', wsId);
            // Seed two environments under the workspace-scoped key so
            // the env-diff scene captures a populated Compare tab
            // rather than an empty Environments rail. Staging carries
            // the production-flavour values; preview overrides two of
            // them so the diff has both `same` and `differs` rows.
            var envs = [
                {
                    id: 'env_staging',
                    name: 'staging',
                    color: '#22c55e',
                    variables: {
                        baseUrl: 'https://api.staging.harbor.example',
                        apiKey: 'st-c4f1-d29a-7b00',
                        region: 'eu-central-1',
                        portCallDefaultLimit: '50'
                    }
                },
                {
                    id: 'env_preview',
                    name: 'preview',
                    color: '#f59e0b',
                    variables: {
                        baseUrl: 'https://api.preview.harbor.example',
                        apiKey: 'pv-78a2-13fb-91c0',
                        region: 'eu-central-1',
                        portCallDefaultLimit: '200'
                    }
                }
            ];
            localStorage.setItem('bowire_ws_' + wsId + '_environments',
                JSON.stringify(envs));
            localStorage.setItem('bowire_ws_' + wsId + '_active_env', 'env_staging');
        } catch {}
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
    // v2.0: Flows is a rail mode (#137 dispatch). The sidebar's
    // unified-toolbar `+` (.bowire-sidebar-toolbar-primary) is the
    // create-flow entry point; legacy #bowire-flow-create-btn IDs
    // were retired with the toolbar consolidation.
    log('flow-editor…');
    await clickRail(page, 'flows');
    await page.waitForTimeout(500);
    const createFlow = page.locator('.bowire-sidebar-toolbar-primary').first();
    if (await createFlow.isVisible().catch(() => false)) {
        await createFlow.click();
        await page.waitForTimeout(500);
    }
    for (const cls of ['bowire-flow-add-btn-blue', 'bowire-flow-add-btn-orange', 'bowire-flow-add-btn-gray']) {
        const btn = page.locator(`.${cls}`).first();
        if (await btn.isVisible().catch(() => false)) { await btn.click(); await page.waitForTimeout(200); }
    }
    await page.waitForTimeout(400);
    // Flow editor canvas owns the main pane; crop to it so the
    // screenshot demos the visual-flow surface, not the surrounding
    // rail + sidebar that don't belong to the feature.
    await shot(page, 'flow-editor', { selector: '.bowire-flow-canvas' });
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
        await clickMethodItem(page, 'List');
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
        // The feature is "click any JSON value to chain" — crop to
        // the response pane so the screenshot shows the picker
        // affordance + JSON tree, not the whole workbench.
        await shot(page, 'json-chaining', { selector: '[id^="bowire-response-pane"]' });
    } catch (e) {
        log(`  (json-chaining failed: ${e.message.split('\n')[0]})`);
    }
    await resetToSidebar(page);

    // ---- 3) streaming ----
    // WatchCrane is a gRPC server-streaming RPC — ticks come in every
    // second from the sample's background timer.
    log('streaming…');
    await clickMethodItem(page, 'WatchCrane');
    await page.waitForTimeout(800);
    await page.locator('input[data-field-key="craneId"]').first().fill('1');
    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    await page.waitForTimeout(2000);
    // Streaming is a response-pane phenomenon (frame list + detail).
    // Crop to the response pane so the screenshot shows the streaming
    // surface only — the protocols.html consumer's alt text talks
    // about the "frame pane", that's exactly what we capture.
    await shot(page, 'streaming', { selector: '[id^="bowire-response-pane"]' });
    // Stop the stream so the next shot isn't racing against incoming frames.
    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});
    await page.waitForTimeout(800);
    await resetToSidebar(page);
    await page.waitForTimeout(500);
    await resetToSidebar(page);

    // ---- 4) command-palette ----
    // v2.0: the omnibox is a modal overlay. The input
    // (#bowire-command-palette-input) only exists inside the modal
    // and the modal only opens when the trigger button is clicked or
    // Ctrl/Cmd+K is pressed.
    try {
        log('command-palette…');
        await page.keyboard.press('Control+K');
        await page.waitForSelector('#bowire-command-palette-input', { timeout: 5000 });
        await page.locator('#bowire-command-palette-input').fill('port');
        await page.waitForTimeout(500);
        // Crop to the palette modal so the screenshot shows the
        // command-palette UI rather than the dimmed-out workbench
        // behind it. The modal wraps the input + suggestions in
        // #bowire-topbar-palette which itself sits inside an
        // overlay; the modal-content selector .bowire-omnibox-modal
        // wraps both for the cleanest crop.
        await shot(page, 'command-palette', {
            selector: '.bowire-omnibox-modal, #bowire-topbar-palette',
            padding: 24
        });
        await page.keyboard.press('Escape');
        await page.waitForTimeout(200);
    } catch (e) {
        log(`  (command-palette failed: ${e.message.split('\n')[0]})`);
    }

    // ---- 5) env-diff — DEFERRED to Phase B+ ----
    // v2.0 moved environments behind hideFromRail (#192 — Workspaces
    // tree dispatches to envs; no standalone rail icon). The script
    // can't `clickRail('environments')` because the rail-btn is filtered
    // out of the DOM. Proper capture needs either:
    //   (a) inject via workspaces → workspace-settings → Variables tab,
    //       then click Compare on a named env; OR
    //   (b) extend Bowire to expose env-detail navigation on a window-
    //       global hook like `__bowireApi.openEnv(envId, 'compare')`.
    // Skipping the capture for now; existing env-diff.png stays from
    // previous run.
    log('env-diff — DEFERRED (env rail is hideFromRail in v2.0; needs workspaces-tree navigation). scene skipped.');

    // ---- 6) settings — DEFERRED to Phase B+ ----
    // openSettings is IIFE-scoped (not on window); the UI opens via
    // App-Drawer click. Proper Phase B fix: open the App-Drawer + click
    // its Settings entry. Skipped here so the rest of the run produces
    // clean crops; existing settings.png stays from previous run.
    log('settings — DEFERRED (openSettings not on window in v2.0; needs App-Drawer click path). scene skipped.');

    // ---- 7) recording ----
    // v2.0: Recordings live on their own rail mode (#137 dispatch).
    // The Discover-toolbar's record toggle was retired with the
    // unified-sidebar-toolbar rollout (#426); the start/stop button is
    // now in the Recordings rail's sidebar toolbar.
  try {
    log('recording…');
    await clickRail(page, 'recordings');
    await page.waitForTimeout(500);
    const recStart = page.locator('.bowire-sidebar-toolbar-primary').first();
    if (await recStart.isVisible().catch(() => false)) {
        await recStart.click();
        await page.waitForTimeout(500);

        // Pop back into Discover, hit two unary methods, then return.
        await clickRail(page, 'discover');
        await page.waitForTimeout(400);
        await clickMethodItem(page, 'GetPortCall');
        await page.waitForTimeout(600);
        const recId = page.locator('input[data-field-key="portCallId"], input[data-field-key="id"]').first();
        if (await recId.isVisible().catch(() => false)) await recId.fill('1');
        await page.locator('.bowire-execute-btn').first().click();
        await page.waitForTimeout(1200);

        await clickMethodItem(page, 'SchedulePortCall');
        await page.waitForTimeout(600);
        const shipId = page.locator('input[data-field-key="shipId"]').first();
        if (await shipId.isVisible().catch(() => false)) await shipId.fill('1');
        const dockNum = page.locator('input[data-field-key="dockNumber"]').first();
        if (await dockNum.isVisible().catch(() => false)) await dockNum.fill('1');
        const arrivalTs = page.locator('input[data-field-key="scheduledArrivalUnixS"]').first();
        if (await arrivalTs.isVisible().catch(() => false)) {
            await arrivalTs.fill(String(Math.floor(Date.now() / 1000) + 3600));
        }
        await page.locator('.bowire-execute-btn').first().click();
        await page.waitForTimeout(1200);

        // Back to Recordings rail to stop + capture the populated state.
        await clickRail(page, 'recordings');
        await page.waitForTimeout(400);
        const recStop = page.locator('.bowire-sidebar-toolbar-primary').first();
        if (await recStop.isVisible().catch(() => false)) await recStop.click();
        // Wait for the recording detail pane to render the populated
        // step rows so the crop captures the timeline, not an empty
        // selection.
        await page.waitForSelector('.bowire-recording-detail .bowire-recording-step-row, .bowire-recording-detail-header', { timeout: 4000 }).catch(() => {});
        await page.waitForTimeout(400);
        await shot(page, 'recording', { selector: '.bowire-recording-detail' });
    } else {
        log('  (recordings rail toolbar primary not found — leaving placeholder)');
    }
  } catch (e) {
    log(`  (recording failed: ${e.message.split('\n')[0]})`);
  }

    // ---- 8) performance — RETIRED in v2.0 (#417) ----
    // The Performance tab was removed from the response-pane in favour
    // of the Benchmarks rail's envelope architecture (#131). The
    // capture is skipped; pick up the Benchmark envelope-detail-pane
    // as a replacement scene in the follow-up capture pass.
    // The `performance.png` consumers in site/ + docs/ should switch
    // their references to a new `benchmark-envelope.png` scene that a
    // future capture-screenshots pass will produce.
    log('performance — RETIRED in v2.0; scene skipped (#417 / #131 envelope arch).');
    await resetToSidebar(page);

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
        //
        // v2.0: localStorage.clear() also wipes the workspace seed +
        // rail mode + workspace-scoped URL key. Without re-seeding,
        // the render-main force-home rule (no active workspace AND
        // workspaces.length === 0) kicks in and rewrites the rail
        // back to 'home' on the very first paint — every "ready /
        // method-detail / editable / wrong-protocol" shot times out
        // because the Discover sidebar never attaches. Re-seed:
        //   - bowire_workspaces (single capture workspace)
        //   - bowire_active_workspace
        //   - bowire_rail_mode = discover
        //   - bowire_theme_pref
        //   - bowire_ws_<id>_server_urls (workspace-scoped key —
        //     the legacy bowire_server_urls is an orphan-namespace
        //     write that prologue.js's wsKey() routes around).
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
                var wsId = 'ws_capture';
                localStorage.setItem('bowire_workspaces', JSON.stringify([{
                    id: wsId, name: 'Capture', color: '#6366f1',
                    createdAt: Date.now(), lastOpenedAt: Date.now()
                }]));
                localStorage.setItem('bowire_active_workspace', wsId);
                localStorage.setItem('bowire_rail_mode', 'discover');
                localStorage.setItem('bowire_theme_pref', theme);
                // Workspace-scoped server URLs (matches wsKey() route)
                localStorage.setItem('bowire_ws_' + wsId + '_server_urls', urlsJson);
                // Legacy fallback in case any reader still consults the
                // unscoped key.
                localStorage.setItem('bowire_server_urls', urlsJson);
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
        // Same DOM-attached / not-yet-visible distinction as the
        // top-of-script wait — collapsed groups would block a
        // visibility wait until expandAllGroups runs below.
        await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 30000 });
        await expandAllGroups(page);
        await page.waitForTimeout(800);
        await shotQuickstart(page, 'ready');

        // method-detail: pick the first method, full pane visible.
        // Synthetic DOM .click() bypasses the collapsed-group visibility
        // wall the same way clickMethodItem's last-resort branch does.
        const clicked = await page.evaluate(() => {
            const first = document.querySelector('.bowire-method-item');
            if (!first) return false;
            first.click();
            return true;
        });
        if (clicked) {
            await page.waitForTimeout(800);
            await shotQuickstart(page, 'method-detail');
        }

        // editable: empty edit state — open a fresh "new request" from
        // the unified-toolbar primary. The legacy `#bowire-adhoc-btn`
        // was retired; in v2.0 the same affordance is the sidebar
        // toolbar's `+` (.bowire-sidebar-toolbar-primary) which opens
        // the protocol-picker dropdown. Click it once to surface the
        // dropdown for the screenshot; if the dropdown auto-picks a
        // protocol the editor lands on the empty-state.
        const newBtn = page.locator('.bowire-sidebar-toolbar-primary').first();
        if (await newBtn.isVisible().catch(() => false)) {
            await newBtn.click();
            await page.waitForTimeout(500);
            await shotQuickstart(page, 'editable');
            await page.keyboard.press('Escape').catch(() => {});
        } else {
            // fallback: same ready frame as a placeholder for editable
            await shotQuickstart(page, 'editable');
        }

        // wrong-protocol: type a query that filters everything out so
        // the empty-state lands. v2.0 retired the sidebar-search input;
        // the command-palette (Cmd+K, #bowire-command-palette-input) is
        // now the cross-pane search. We narrow that one to a string no
        // method will match, then shoot the empty results state.
        const search = page.locator('#bowire-command-palette-input').first();
        if (await search.isVisible().catch(() => false)) {
            await search.click();
            await page.keyboard.type('zzzzzz', { delay: 30 });
            await page.waitForTimeout(500);
            await shotQuickstart(page, 'wrong-protocol');
            await page.keyboard.press('Escape').catch(() => {});
        } else {
            await shotQuickstart(page, 'wrong-protocol');
        }
    } catch (e) {
        log(`  (ready/method-detail/editable/wrong-protocol failed: ${e.message.split('\n')[0]})`);
    }

    // ---- 11) AI-feature shots (#25 / #59 / #60 / #62) ----
    // The marketing CI runs without an Ollama provider, so we seed the
    // panel state via the __bowireAi._seed* hooks instead of hitting a
    // real model. Each shot drives one of the AI surfaces into a
    // deterministic demo state and captures it.
    //
    // v2.0 RE-WIRE NEEDED: AI is no longer a response-pane tab — it
    // moved into the unified right-drawer (#90 / #115 Drawer-Primitive).
    // The legacy `#bowire-response-tab-ai` click below has been
    // replaced with a right-drawer tab activation. Threat model lives
    // on the Security RAIL now (not the AI drawer), so `ai-threat-
    // model.png` should be re-shot from the Security surface instead;
    // it's left running here for now but the produced image will need
    // narrative-side relabelling. Template-suggest + fuzz-values seed
    // hooks survive; their panel mount selectors may differ — verify
    // visually before publishing.
    try {
        // Reopen the main workbench + select a method so the response
        // pane (which hosts the AI tab) is rendered.
        await page.goto(URL, { waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 15000 });
        await page.waitForTimeout(500);
        await expandAllGroups(page);
        const firstMethod = page.locator('.bowire-method-item').first();
        if (await firstMethod.isVisible().catch(() => false)) {
            await firstMethod.click().catch(() => {});
            await page.waitForTimeout(500);
        }
        // v2.0: AI lives on the right drawer's Assistant tab.
        // #bowire-right-drawer-tab-assistant fires the drawer open +
        // selects the tab in one click.
        const aiTab = page.locator('#bowire-right-drawer-tab-assistant').first();
        if (await aiTab.isVisible().catch(() => false)) {
            await aiTab.click();
            await page.waitForTimeout(300);
        }

        // Seed a "connected to ollama llama3.2:3b" status so the panel
        // shows the live state instead of the install hint.
        await page.evaluate(() => {
            const ai = window.__bowireAi;
            if (!ai || !ai._seedStatus) return;
            ai._seedStatus({
                hasClient: true,
                hostManaged: false,
                providerId: 'ollama',
                endpoint: 'http://localhost:11434',
                model: 'llama3.2:3b',
                autoDetectLocal: true
            });
            ai._seedProbe({
                ollama: {
                    endpoint: 'http://localhost:11434',
                    provider: 'ollama',
                    models: ['llama3.2:3b', 'qwen2.5:7b', 'codellama:13b']
                },
                lmstudio: null
            });
            ai._seedChat([
                { role: 'user', content: 'Why is GetPortCall returning empty bodies for IDs > 1000?' },
                { role: 'assistant', content: 'The response schema declares `manifest` as required, but the seed data only populates IDs 1-1000. Either widen the seed or relax the schema.' }
            ]);
            // Re-render the panel so the seeded state shows.
            const panel = document.querySelector('.bowire-ai-panel');
            if (panel) {
                const rendered = ai.renderPanel();
                panel.parentNode.replaceChild(rendered, panel);
            }
        });
        await page.waitForTimeout(400);
        await shot(page, 'ai-panel');

        // Threat-model: seed a ranked list with mixed-risk endpoints
        // so the colour-coded scores render across the spectrum.
        await page.evaluate(() => {
            const ai = window.__bowireAi;
            if (!ai || !ai._seedThreatModel) return;
            const endpointIndex = {
                e1: { endpointId: 'e1', path: '/api/orders/{id}', verb: 'GET', protocol: 'rest', service: 'OrdersService', serverUrl: 'https://api.example.com' },
                e2: { endpointId: 'e2', path: '/api/admin/users', verb: 'DELETE', protocol: 'rest', service: 'AdminService', serverUrl: 'https://api.example.com' },
                e3: { endpointId: 'e3', path: '/api/files/{path}', verb: 'GET', protocol: 'rest', service: 'FilesService', serverUrl: 'https://api.example.com' },
                e4: { endpointId: 'e4', path: '/api/auth/reset-password', verb: 'POST', protocol: 'rest', service: 'AuthService', serverUrl: 'https://api.example.com' },
                e5: { endpointId: 'e5', path: '/api/products', verb: 'GET', protocol: 'rest', service: 'CatalogService', serverUrl: 'https://api.example.com' }
            };
            ai._seedThreatModel({
                inputCount: 47,
                modelId: 'llama3.2:3b',
                endpointIndex,
                ranked: [
                    { endpointId: 'e2', risk: 9, why: 'Unauthenticated DELETE on admin path — IDOR + auth-bypass surface', suggestedTemplates: ['auth-bypass', 'idor'] },
                    { endpointId: 'e3', risk: 8, why: 'Path parameter flows into a file lookup — classic path-traversal risk', suggestedTemplates: ['path-traversal'] },
                    { endpointId: 'e1', risk: 7, why: 'Resource-id path with no scoping; classic IDOR candidate', suggestedTemplates: ['idor', 'mass-assignment'] },
                    { endpointId: 'e4', risk: 5, why: 'Password-reset POST — rate limit + email-injection worth a probe', suggestedTemplates: ['injection-template'] },
                    { endpointId: 'e5', risk: 2, why: 'Read-only listing, no parameters', suggestedTemplates: [] }
                ]
            });
            const panel = document.querySelector('.bowire-ai-panel');
            if (panel) {
                const rendered = ai.renderPanel();
                panel.parentNode.replaceChild(rendered, panel);
            }
        });
        await page.waitForTimeout(400);
        await shot(page, 'ai-threat-model');

        // Template-suggest: same threat-model state, plus a seeded YAML
        // output box on the top row so the generator's expanded shape
        // is visible.
        await page.evaluate(() => {
            const ai = window.__bowireAi;
            if (!ai || !ai._seedTemplate) return;
            const yaml = [
                'id: auth-bypass-admin-users',
                'info:',
                '  name: Unauthenticated DELETE on /api/admin/users',
                '  author: bowire-ai',
                '  severity: high',
                '  tags: [auth-bypass, ai-suggested, bowire]',
                'http:',
                '  - method: DELETE',
                '    path:',
                '      - "{{BaseURL}}/api/admin/users"',
                '    matchers-condition: and',
                '    matchers:',
                '      - type: status',
                '        status: [200, 204]',
                '      - type: word',
                '        part: body',
                '        words: ["deleted"]'
            ].join('\n');
            ai._seedTemplate('e2', {
                yaml,
                suggestedFilename: 'bowire-ai-api-admin-users-auth-bypass.yaml',
                modelId: 'llama3.2:3b'
            });
        });
        await page.waitForTimeout(300);
        await shot(page, 'ai-template-suggest');

        // Fuzz-values: open the picker panel with a realistic mix of
        // boundary inputs that show all three severity badges.
        await page.evaluate(() => {
            const sm = window.__bowireSemanticsMenu;
            if (!sm || !sm._seedFuzzPicker) return;
            sm._seedFuzzPicker(
                { jsonPath: '$.order.itemCount' },
                [
                    { value: 0, why: 'boundary: zero', severity: 'info' },
                    { value: -1, why: 'boundary: negative on uint field', severity: 'low' },
                    { value: 2147483648, why: 'int32 overflow', severity: 'medium' },
                    { value: '9999999999999999999999', why: 'stringified overflow', severity: 'medium' },
                    { value: null, why: 'explicit null', severity: 'low' },
                    { value: 3.14, why: 'float into int field', severity: 'low' },
                    { value: '0x7F', why: 'hex-encoded boundary', severity: 'info' },
                    { value: true, why: 'wrong-type coercion', severity: 'low' },
                    { value: 'NaN', why: 'IEEE-754 sentinel as string', severity: 'low' },
                    { value: 1e308, why: 'double overflow', severity: 'medium' }
                ],
                'itemCount'
            );
        });
        await page.waitForTimeout(400);
        await shot(page, 'ai-fuzz-values');
    } catch (e) {
        log(`  (AI feature shots failed: ${e.message.split('\n')[0]})`);
    }

    await ctx.close();
    await browser.close();
    log('done');
})();
