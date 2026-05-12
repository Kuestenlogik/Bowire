/**
 * Records a Bowire demo video by driving the UI with Playwright.
 *
 * Usage:
 *   cd Bowire.Samples && dotnet run --project src/Kuestenlogik.Bowire.Samples.Combined  (in one terminal)
 *   node scripts/record-demo.js                                                          (in another)
 *
 * Output:
 *   site/assets/videos/bowire-demo-{dark,light}.webm
 *   site/assets/videos/bowire-demo.webm (default fallback, mirrors dark)
 *
 * Demo flow — five beats designed to showcase the workbench in ~60s:
 *   1) unary REST call           → method picked, Execute clicked, response card lands
 *   2) gRPC server-stream        → WatchCrane invoked, stream rows tick in
 *   3) performance test          → repeat 25× with concurrency, histogram + P50/P90/P99 render
 *   4) visual flow editor        → switch sidebar to flows, see the node canvas
 *   5) recording                 → toggle Record, fire a call, stop, see the captured step
 *
 * Every beat WAITS for the visible result (DOM-selector based) instead
 * of relying on fixed timeouts — previous versions jumped to the next
 * beat before the response landed, which read as "wait, did anything
 * happen?" to the viewer.
 */

const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const BOWIRE_URL = 'https://localhost:5101/bowire';
const OUTPUT_DIR = path.resolve(__dirname, '..', 'site', 'assets', 'videos');
const WIDTH  = 1280;
const HEIGHT = 720;
const THEME  = (process.env.THEME || 'dark').toLowerCase();

function log(msg) { console.log(new Date().toISOString().slice(11, 19), msg); }

/** Resolve when ANY selector in `selectors` becomes visible, or after `timeoutMs`. */
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

(async () => {
    fs.mkdirSync(OUTPUT_DIR, { recursive: true });

    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({
        viewport:    { width: WIDTH, height: HEIGHT },
        recordVideo: { dir: OUTPUT_DIR, size: { width: WIDTH, height: HEIGHT } },
        ignoreHTTPSErrors: true,
        colorScheme: THEME
    });
    const page = await context.newPage();
    await page.emulateMedia({ colorScheme: THEME });

    page.on('console', msg => {
        if (msg.type() === 'error') log(`[browser ERR] ${msg.text()}`);
    });

    try {
        log(`Navigating to Bowire UI (theme=${THEME})…`);
        await page.goto(BOWIRE_URL, { waitUntil: 'domcontentloaded' });
        await page.evaluate((t) => {
            try { localStorage.setItem('bowire_theme_pref', t); } catch {}
        }, THEME);
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app.bowire-app-ready', { timeout: 20000 });
        // Wait for method items to be ATTACHED (services may render
        // collapsed by default so no item is visible until we expand).
        await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 30000 });
        await page.waitForTimeout(1500);
        log('Services discovered.');

        // Expand every collapsed service group so methods are visible
        // and clickable. The expanded state lives on the chevron span
        // (.bowire-service-chevron.expanded), not on the group div, so
        // the previous .bowire-service-group.collapsed selector never
        // matched anything and the demo silently moved on with all
        // services still folded. Click the header of every group whose
        // chevron lacks the expanded class.
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
        await page.waitForTimeout(500);

        // ---- Beat 1: unary REST call → response ----
        log('Beat 1: unary REST → response');
        let beat1Done = false;
        const restMethod = page.locator('.bowire-method-item', { hasText: 'List' }).first();
        if (await restMethod.isVisible().catch(() => false)) {
            await restMethod.click();
            await page.waitForTimeout(900);
            const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
            if (await exec.isEnabled().catch(() => false)) {
                await exec.click();
                // Wait for response content — either the JSON tree, the
                // formatted-result card, or an obvious error box. Bowire
                // renders one of these as soon as the response arrives.
                const hit = await waitForAny(page, [
                    '.bowire-response-output.is-interactive',
                    '.bowire-response-output.error',
                    '.bowire-json-tree',
                    '.bowire-response-output:has(pre)',
                ], 8000);
                log(`  REST response landed (${hit || 'timeout'}).`);
                // Let the viewer read it.
                await page.waitForTimeout(2200);
                beat1Done = !!hit;
            }
        }
        if (!beat1Done) log('  beat 1 produced no visible response — continuing anyway');

        // ---- Beat 2: gRPC server-streaming WatchCrane → stream rows ----
        log('Beat 2: gRPC server-stream → WatchCrane');
        const stream = page.locator('.bowire-method-item', { hasText: 'WatchCrane' }).first();
        if (await stream.isVisible().catch(() => false)) {
            await stream.scrollIntoViewIfNeeded();
            await stream.click();
            await page.waitForTimeout(900);
            const craneId = page.locator('input[data-field-key="craneId"]').first();
            if (await craneId.isVisible().catch(() => false)) {
                await craneId.click();
                await craneId.fill('1');
                await page.waitForTimeout(300);
            }
            const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
            if (await exec.isEnabled().catch(() => false)) {
                await exec.click();
                // Wait for the FIRST stream frame to appear.
                // Actual list-item class is .bowire-stream-list-item
                // (perf-diff.js uses 'row' — render-main.js uses 'list-item').
                const firstRow = await waitForAny(page, [
                    '.bowire-stream-list-item',
                    '.bowire-stream-list-pane .bowire-stream-list-item'
                ], 10000);
                log(`  First stream frame landed (${firstRow || 'timeout'}).`);
                // Let 3-4 more frames tick in at 1 Hz so the count badge
                // animates visibly.
                await page.waitForTimeout(4500);
                log('  Stream rows accumulating.');
            }
            // Stop the stream so the recording closes cleanly.
            const cancel = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
            if (await cancel.isVisible().catch(() => false)) {
                await cancel.click().catch(() => {});
                await page.waitForTimeout(600);
            }
        }

        // ---- Beat 3: performance test → histogram + P-percentiles ----
        log('Beat 3: performance test → histogram');
        // Go back to a unary method (Performance tab only shows for unary).
        const unaryForPerf = page.locator('.bowire-method-item', { hasText: 'List' }).first();
        if (await unaryForPerf.isVisible().catch(() => false)) {
            await unaryForPerf.click();
            await page.waitForTimeout(900);
            // Click the Performance tab (next to Response / Headers).
            const perfTab = page.locator('.bowire-tab', { hasText: 'Performance' }).first();
            if (await perfTab.isVisible().catch(() => false)) {
                await perfTab.click();
                // Wait for the perf-pane content to actually attach
                // before trying to fill its inputs; the tab switch
                // triggers a render() which doesn't complete synchronously.
                await page.waitForSelector('#bowire-perf-calls-input', { state: 'visible', timeout: 6000 })
                    .catch(() => log('  perf calls input never showed'));
                await page.waitForTimeout(400);
                // Bump the call count up a bit so the histogram has shape.
                const calls = page.locator('#bowire-perf-calls-input').first();
                if (await calls.isVisible().catch(() => false)) {
                    await calls.fill('25', { timeout: 3000 }).catch(() => {});
                }
                const conc = page.locator('#bowire-perf-concurrency-input').first();
                if (await conc.isVisible().catch(() => false)) {
                    await conc.fill('5', { timeout: 3000 }).catch(() => {});
                }
                const runBtn = page.locator('#bowire-perf-run-btn').first();
                if (await runBtn.isVisible().catch(() => false)) {
                    await runBtn.click();
                    // Wait for the stats grid / progress bar — those
                    // are the actual class names perf-diff.js renders.
                    // Earlier selector guesses ('histogram', 'summary',
                    // 'percentile', 'result') never matched, so the
                    // beat used to leave the perf-tab without ever
                    // confirming a result rendered.
                    const hit = await waitForAny(page, [
                        '.bowire-perf-stats',
                        '.bowire-perf-stat-value',
                        '.bowire-perf-progress-wrap'
                    ], 12000);
                    log(`  Histogram rendered (${hit || 'timeout'}).`);
                    // Let the viewer read the P50/P90/P99 numbers + chart.
                    await page.waitForTimeout(2500);
                }
            } else {
                log('  Performance tab not visible — skipping');
            }
        }

        // ---- Beat 4: visual flow editor ----
        log('Beat 4: visual flow editor');
        // Open the sidebar "+ New" dropdown and pick Flow.
        const newBtn = page.locator('.bowire-sidebar-new-btn, #bowire-sidebar-new-btn').first();
        if (await newBtn.isVisible().catch(() => false)) {
            await newBtn.click();
            await page.waitForTimeout(500);
            const flowItem = page.locator('.bowire-new-dropdown-item', { hasText: 'Flow' }).first();
            if (await flowItem.isVisible().catch(() => false)) {
                await flowItem.click();
                // Wait for the flow canvas to render.
                const hit = await waitForAny(page, [
                    '.bowire-flow-canvas',
                    '[id^="bowire-flow-canvas-"]',
                    '#bowire-flow-canvas-empty'
                ], 8000);
                log(`  Flow canvas rendered (${hit || 'timeout'}).`);
                await page.waitForTimeout(2800);
            } else {
                log('  Flow dropdown item not found — skipping flow beat');
            }
        } else {
            // Fallback — try a sidebar tab labelled "Flows" if it exists
            const flowsTab = page.locator('text=Flows').first();
            if (await flowsTab.isVisible().catch(() => false)) {
                await flowsTab.click();
                await page.waitForTimeout(2500);
            }
        }

        // ---- Beat 5: recording → capture a call ----
        log('Beat 5: recording → capture a call');
        // Get back to the Services view so the Record button + Execute
        // both make sense in frame. After Beat 4 the sidebar is on the
        // flows view; clicking the Services pill takes a render cycle
        // to swap the sidebar contents back, so wait for a method item
        // to be visible (state: 'attached' alone isn't enough — we
        // need to actually click one in the next step).
        // Use the exact view-pill id so we don't accidentally match
        // some other 'Services' label elsewhere on the page.
        const servicesTab = page.locator('#bowire-view-pill-services').first();
        if (await servicesTab.isVisible().catch(() => false)) {
            await servicesTab.click();
            await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 5000 })
                .catch(() => log('  services view never re-rendered'));
            // Service groups collapse again when we leave + return, so
            // re-expand any that closed up.
            const groupsAgain = await page.locator('.bowire-service-group').all();
            for (const group of groupsAgain) {
                const chev = group.locator('.bowire-service-chevron').first();
                const cls = await chev.getAttribute('class').catch(() => '');
                if (!cls || !cls.includes('expanded')) {
                    await group.locator('.bowire-service-header').first().click().catch(() => {});
                    await page.waitForTimeout(80);
                }
            }
            await page.waitForTimeout(400);
        }
        // Pre-select a method BEFORE starting recording so the
        // method+request pane is already populated when the viewer's
        // eye lands on the screen. Earlier ordering put the recording
        // toggle first then tried to find a method-item — sometimes
        // the service groups were still folding/refolding during the
        // view switch, the .bowire-method-item locator returned
        // not-visible, and the recording beat collapsed to a bare
        // armed→stopped toggle with nothing in between.
        const recordMethod = page.locator('.bowire-method-item', { hasText: 'List' }).first();
        await recordMethod.waitFor({ state: 'visible', timeout: 5000 })
            .catch(() => log('  no visible list method to record against'));
        if (await recordMethod.isVisible().catch(() => false)) {
            await recordMethod.click();
            // Make sure we're on Response, not the Performance tab from Beat 3.
            const respTab = page.locator('.bowire-tab', { hasText: 'Response' }).first();
            if (await respTab.isVisible().catch(() => false)) {
                await respTab.click().catch(() => {});
                await page.waitForTimeout(300);
            }
        }
        const recordStart = page.locator('#bowire-recording-start-btn').first();
        if (await recordStart.isVisible().catch(() => false)) {
            await recordStart.click();
            await page.waitForTimeout(900);
            log('  Recording armed.');
            // Fire the request — step lands on the recording's tally.
            const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
            if (await exec.isEnabled().catch(() => false)) {
                await exec.click();
                await waitForAny(page, [
                    '.bowire-response-output.is-interactive',
                    '.bowire-response-output.error',
                    '.bowire-json-tree'
                ], 8000);
                // Let the step-count badge animate to '1' next to the
                // recording toggle.
                await page.waitForTimeout(1800);
                log('  Recording captured a step.');
            }
            // Stop recording — keeps the step-count visible briefly.
            const recordStop = page.locator('#bowire-recording-stop-btn').first();
            if (await recordStop.isVisible().catch(() => false)) {
                await recordStop.click();
                await page.waitForTimeout(2000);
                log('  Recording stopped.');
            }
        } else {
            log('  Record button not visible — skipping recording beat');
        }

        log('Finalising…');
        await page.waitForTimeout(800);
    } catch (e) {
        log(`✗ ${e.message.split('\n')[0]}`);
        try {
            await page.screenshot({ path: path.join(OUTPUT_DIR, 'debug-fatal.png'), fullPage: true });
        } catch {}
    }

    // Close context to flush the video to disk
    await context.close();
    await browser.close();

    // Find the latest webm and rename it to bowire-demo.webm
    const files = fs.readdirSync(OUTPUT_DIR)
        .filter(f => f.endsWith('.webm'))
        .map(f => ({ f, t: fs.statSync(path.join(OUTPUT_DIR, f)).mtimeMs }))
        .sort((a, b) => b.t - a.t);
    if (files.length > 0) {
        const src = path.join(OUTPUT_DIR, files[0].f);
        const dst = path.join(OUTPUT_DIR, `bowire-demo-${THEME}.webm`);
        if (src !== dst) fs.renameSync(src, dst);
        const sz = (fs.statSync(dst).size / 1024).toFixed(1);
        log(`✓ Video saved: ${dst} (${sz} KB)`);
        if (THEME === 'dark') {
            // Default fallback for any markup not yet upgraded to the
            // theme-aware <source media="..."> pattern.
            const fallback = path.join(OUTPUT_DIR, 'bowire-demo.webm');
            fs.copyFileSync(dst, fallback);
            log(`  + fallback ${fallback}`);
        }
    } else {
        log('✗ No .webm file produced');
    }
})();
