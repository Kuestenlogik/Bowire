/**
 * Captures protocol-specific screenshots for the home-page protocol-card
 * popups for the built-in streaming protocols. Drives a locally-running
 * sample via Playwright, picks a server-stream method, waits for frames,
 * shoots dark + light, then moves on.
 *
 * Each entry:
 *   url        — the sample's /bowire URL
 *   methodText — text on the .bowire-method-item to click (must be unique
 *                or first-match; pass a more specific string if needed)
 *   waitMs     — how long to wait after Execute before screenshotting
 *   shotName   — base PNG name (theme suffix appended)
 *
 * Set THEME=dark or THEME=light. Run twice for a dark + light pair.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const THEME = (process.env.THEME || 'dark').toLowerCase();

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

async function capture({ url, methodText, waitMs, shotName }) {
    log(`---- ${shotName} (${THEME}) ----`);
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        deviceScaleFactor: 2,
        ignoreHTTPSErrors: true,
        colorScheme: THEME === 'light' ? 'light' : 'dark',
    });
    const page = await ctx.newPage();
    await page.goto(url, { waitUntil: 'networkidle' });
    await page.evaluate((theme) => { try { localStorage.setItem('bowire-theme', theme); } catch (_) {} }, THEME);
    await page.reload({ waitUntil: 'networkidle' });

    // Wait for the sidebar to render method items into the DOM. We use
    // 'attached' rather than the default 'visible' because some sample
    // hosts ship more than 5 services and Bowire keeps groups collapsed
    // by default — the items are in the DOM but display:none.
    await page.waitForSelector('.bowire-method-item', { state: 'attached', timeout: 30000 });
    log('  sidebar populated');

    // Expand every service group so the target method is visible. Cheap
    // and idempotent: clicking a header that's already expanded toggles
    // it shut, but we click before any expanded state has been set.
    const headers = await page.locator('.bowire-service-header').all();
    for (const h of headers) {
        const expanded = await h.locator('.bowire-service-chevron.expanded').count();
        if (expanded === 0) await h.click().catch(() => {});
    }
    await page.waitForTimeout(300);

    await page.locator('.bowire-method-item', { hasText: methodText }).first().click();
    await page.waitForTimeout(400);

    // Sample servers usually need at least one valid id in the form
    // (gRPC WatchCrane needs an existing crane_id). Fill every empty
    // text/number input in the request pane with "1" so a default
    // Execute hits the first seeded record. Skip selects / checkboxes
    // / readonly.
    const inputs = await page.locator('.bowire-pane input, .bowire-pane textarea').all();
    for (const inp of inputs) {
        const tag = await inp.evaluate(e => e.tagName);
        const type = (await inp.getAttribute('type') || '').toLowerCase();
        const readonly = await inp.getAttribute('readonly');
        const value = await inp.inputValue().catch(() => '');
        if (readonly !== null || value) continue;
        if (tag === 'INPUT' && (type === 'checkbox' || type === 'radio' || type === 'select' || type === 'submit' || type === 'button')) continue;
        await inp.fill('1').catch(() => {});
    }

    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log(`  stream started — waiting ${waitMs}ms`);
    await page.waitForTimeout(waitMs);

    const file = path.join(OUT, `${shotName}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${shotName}-${THEME}.png`));
    log(`  -> ${shotName}-${THEME}.png`);

    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});

    await browser.close();
}

(async () => {
    // Pull the target list from argv. CLI: node capture-builtin-screenshots.js grpc signalr
    const wanted = new Set(process.argv.slice(2));
    const targets = {
        grpc:    { url: 'https://localhost:5101/bowire', methodText: 'WatchCrane',         waitMs: 5000, shotName: 'streaming-grpc' },
        signalr: { url: 'https://localhost:5101/bowire', methodText: 'SubscribeToChanges', waitMs: 5000, shotName: 'streaming-signalr' },
        graphql: { url: 'http://localhost:5104/bowire',  methodText: 'shipPositions',       waitMs: 5000, shotName: 'streaming-graphql' },
        sse:     { url: 'http://localhost:5105/bowire',  methodText: 'cargo-events',        waitMs: 5000, shotName: 'streaming-sse' },
        ws:      { url: 'http://localhost:5106/bowire',  methodText: 'echo',                waitMs: 5000, shotName: 'streaming-websocket' },
        mqtt:    { url: 'http://localhost:5107/bowire',  methodText: 'subscribe',           waitMs: 5000, shotName: 'streaming-mqtt' },
        socketio:{ url: 'http://localhost:5108/bowire',  methodText: 'subscribe',           waitMs: 5000, shotName: 'streaming-socketio' },
    };
    const list = wanted.size > 0
        ? [...wanted].map(k => ({ key: k, ...targets[k] })).filter(t => t.url)
        : Object.entries(targets).map(([k, v]) => ({ key: k, ...v }));
    for (const t of list) {
        try {
            await capture(t);
        } catch (e) {
            console.error(`  FAILED ${t.shotName}: ${e.message}`);
        }
    }
})().catch(err => { console.error(err); process.exit(1); });
