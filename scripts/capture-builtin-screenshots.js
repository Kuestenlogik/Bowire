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

// Node-side fetch traffic generators (e.g. graphql) hit the sample over
// https with a self-signed dev cert. Skip cert validation for the
// capture process so the mutation goes through.
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const THEME = (process.env.THEME || 'dark').toLowerCase();

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

async function capture(target) {
    const { url, methodText, waitMs, shotName } = target;
    log(`---- ${shotName} (${THEME}) ----`);
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        deviceScaleFactor: 2,
        ignoreHTTPSErrors: true,
        colorScheme: THEME === 'light' ? 'light' : 'dark',
    });
    const page = await ctx.newPage();
    // Some samples (GraphQL with HotChocolate's ws subscriptions, the
    // Combined sample with SignalR + WebSocket open) keep an idle
    // socket open on page load and `networkidle` never resolves —
    // wait for DOM ready instead and let the sidebar populate via the
    // .bowire-method-item attached check below.
    await page.goto(url, { waitUntil: 'domcontentloaded' });
    await page.evaluate((theme) => { try { localStorage.setItem('bowire-theme', theme); } catch (_) {} }, THEME);
    await page.reload({ waitUntil: 'domcontentloaded' });

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

    // Sample servers occasionally need at least one valid id in the
    // form (gRPC WatchCrane needs an existing crane_id). Fill empty
    // *numeric* inputs with "1" so a default Execute hits the first
    // seeded record. Skip URL / text / select / readonly — those
    // either default to the right value (hub URL on SSE / WebSocket)
    // or aren't safe to populate with a placeholder integer.
    const inputs = await page.locator('.bowire-pane input, .bowire-pane textarea').all();
    for (const inp of inputs) {
        const tag = await inp.evaluate(e => e.tagName);
        const type = (await inp.getAttribute('type') || '').toLowerCase();
        const readonly = await inp.getAttribute('readonly');
        const value = await inp.inputValue().catch(() => '');
        const placeholder = (await inp.getAttribute('placeholder') || '').toLowerCase();
        if (readonly !== null || value) continue;
        if (tag === 'INPUT' && (type === 'checkbox' || type === 'radio' || type === 'select' || type === 'submit' || type === 'button')) continue;
        const looksNumeric = type === 'number' || /int|long|float|double|number|count|id\b|size|length/.test(placeholder);
        if (!looksNumeric) continue;
        await inp.fill('1').catch(() => {});
    }

    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log(`  stream started — waiting ${waitMs}ms`);

    // Optional: parallel HTTP request loop that nudges the sample
    // server into emitting events while the screen is captured (for
    // event-driven streams like SignalR's SubscribeToChanges or
    // GraphQL subscriptions, which sit silent without external
    // traffic). The traffic function is invoked every 1.5 s for the
    // duration of the wait window.
    let trafficStop = null;
    if (typeof trafficUrl === 'function') {
        // forwarded through closure
    }
    if (target.traffic) {
        const tick = () => target.traffic(page).catch(() => {});
        tick(); // immediate first hit so the stream gets a frame ASAP
        trafficStop = setInterval(tick, 1500);
    }

    await page.waitForTimeout(waitMs);
    if (trafficStop) clearInterval(trafficStop);

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
        signalr: {
            url: 'https://localhost:5101/bowire',
            methodText: 'SubscribeToChanges',
            waitMs: 7000,
            shotName: 'streaming-signalr',
            // Parallel PATCH requests against the Combined sample's
            // REST surface so the SignalR subscription receives
            // PortCallChanged events while we capture. Cycles status
            // through the enum so each frame looks distinct.
            traffic: async (page) => {
                // PortCallStatus enum: 0 Scheduled, 1 Approaching, 2 Docked,
                // 3 Departing, 4 Completed, 5 Cancelled. System.Text.Json
                // default expects the integer value, not the string name.
                const statuses = [1, 2, 3, 4];
                const idx = Math.floor(Math.random() * statuses.length);
                await page.evaluate(async (status) => {
                    await fetch('https://localhost:5101/api/port-calls/1/status', {
                        method: 'PATCH',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ status, notes: 'capture run' }),
                    }).catch(() => {});
                }, statuses[idx]);
            },
        },
        graphql: {
            // GraphQL sample doesn't host its own /bowire — it expects a
            // standalone bowire CLI pointed at /graphql. Run the CLI on
            // :5079 with --url https://localhost:5115/graphql before
            // running this target.
            url: 'http://localhost:5079/bowire',
            methodText: 'onPortCallChanged',
            waitMs: 7000,
            shotName: 'streaming-graphql',
            // GraphQL HarborSubscription emits on store.PortCallChanged.
            // Stand-alone GraphQL sample has no REST endpoint to PATCH —
            // we trigger the same store from a GraphQL mutation instead.
            // Node.js-side fetch (not page.evaluate) so CORS doesn't
            // block the cross-origin :5079→:5115 request inside the
            // browser context.
            traffic: async () => {
                const statuses = ['APPROACHING', 'DOCKED', 'DEPARTING', 'COMPLETED'];
                const status = statuses[Math.floor(Math.random() * statuses.length)];
                await fetch('https://localhost:5115/graphql', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ query: `mutation { updatePortCallStatus(id: 1, status: ${status}) { id status } }` }),
                }).catch(() => {});
            },
        },
        sse:     { url: 'https://localhost:5114/bowire', methodText: 'Slow keep-alive',   waitMs: 12000, shotName: 'streaming-sse' },
        ws:      { url: 'https://localhost:5113/bowire', methodText: '/ws/ship-tracker',  waitMs: 8000, shotName: 'streaming-websocket' },
        // MQTT sample is a broker + publisher with no embedded /bowire — point
        // a standalone bowire CLI at it: `bowire --port 5079 --url mqtt://localhost:1883`.
        mqtt:    { url: 'http://localhost:5079/bowire',  methodText: 'harbor/crane/1/status', waitMs: 7000, shotName: 'streaming-mqtt' },
        socketio:{ url: 'https://localhost:5118/bowire', methodText: 'subscribe',          waitMs: 5000, shotName: 'streaming-socketio' },
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
