/**
 * Records a Bowire demo video by driving the UI with Playwright.
 *
 * Usage:
 *   cd Bowire.Samples && dotnet run --project src/Kuestenlogik.Bowire.Samples.Combined  (in one terminal)
 *   node scripts/record-demo.js                                                          (in another)
 *
 * Output:
 *   site/assets/videos/bowire-demo.webm
 *   site/assets/videos/debug-*.png         (only on failure)
 *
 * Demo flow — three beats designed to show the workbench in <30s:
 *   1) unary REST call    → method picked, Execute clicked, response card lands
 *   2) gRPC server-stream → WatchCrane filled & invoked, stream rows tick in
 *   3) duplex SignalR     → connect to PortCallHub, watch periodic events arrive
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
        await page.waitForSelector('.bowire-method-item', { timeout: 30000 });
        await page.waitForTimeout(1500);
        log('Services discovered.');

        // Expand all collapsed service groups so the sidebar reads in
        // one glance.
        for (const g of await page.locator('.bowire-service-group.collapsed .bowire-service-group-header').all()) {
            await g.click().catch(() => {});
        }
        await page.waitForTimeout(800);

        // ---- Beat 1: unary REST call → response ----
        log('Beat 1: unary REST → response');
        const restMethod = page.locator('.bowire-method-item', { hasText: 'GetPort-calls' }).first();
        if (await restMethod.isVisible().catch(() => false)) {
            await restMethod.click();
            await page.waitForTimeout(1500);
            const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
            if (await exec.isEnabled().catch(() => false)) {
                await exec.click();
                await page.waitForTimeout(2500);
                log('  REST response received.');
            }
        } else {
            log('  GetPort-calls not visible — using first DEFAULT method instead');
            const fallback = page.locator('.bowire-method-item').first();
            await fallback.click();
            await page.waitForTimeout(1500);
            const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
            if (await exec.isEnabled().catch(() => false)) {
                await exec.click();
                await page.waitForTimeout(2500);
            }
        }

        // ---- Beat 2: gRPC server-streaming WatchCrane → stream rows ----
        log('Beat 2: gRPC server-stream → WatchCrane');
        const stream = page.locator('.bowire-method-item', { hasText: 'WatchCrane' }).first();
        if (await stream.isVisible().catch(() => false)) {
            await stream.scrollIntoViewIfNeeded();
            await stream.click();
            await page.waitForTimeout(1500);
            const craneId = page.locator('input[data-field-key="craneId"]').first();
            if (await craneId.isVisible().catch(() => false)) {
                await craneId.click();
                await craneId.fill('1');
                await page.waitForTimeout(300);
            }
            const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
            if (await exec.isEnabled().catch(() => false)) {
                await exec.click();
                // Let several tick frames land — the sample emits one
                // crane-status frame every second.
                await page.waitForTimeout(5000);
                log('  Stream frames received.');
            }
            // Stop the stream so the recording closes cleanly.
            const cancel = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
            if (await cancel.isVisible().catch(() => false)) {
                await cancel.click().catch(() => {});
                await page.waitForTimeout(600);
            }
        } else {
            log('  WatchCrane not visible — skipping stream beat');
        }

        // ---- Beat 3: SignalR duplex — connect, watch periodic events ----
        log('Beat 3: SignalR duplex → PortCallHub');
        const hubMethod = page.locator('.bowire-method-item', { hasText: 'GetPort-calls' }).nth(1);
        // Fallback: any SignalR-streaming method (purple/orange icon).
        let dupTarget = hubMethod;
        if (!(await dupTarget.isVisible().catch(() => false))) {
            dupTarget = page.locator('.bowire-method-item', { hasText: 'GetShip-tracker' }).first();
        }
        if (await dupTarget.isVisible().catch(() => false)) {
            await dupTarget.scrollIntoViewIfNeeded();
            await dupTarget.click();
            await page.waitForTimeout(1500);
            // Open channel if there's a Connect button (duplex methods)
            const connect = page.locator('#bowire-channel-connect-btn').first();
            if (await connect.isVisible().catch(() => false)) {
                await connect.click();
                await page.waitForTimeout(2500);
                log('  Channel connected — watching events.');
                // Let the duplex feed run a few seconds.
                await page.waitForTimeout(5000);
            } else {
                // Plain unary method: just execute it once more for visual closure.
                const exec = page.locator('.bowire-execute-btn, #bowire-execute-btn').first();
                if (await exec.isEnabled().catch(() => false)) {
                    await exec.click();
                    await page.waitForTimeout(3000);
                }
            }
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
