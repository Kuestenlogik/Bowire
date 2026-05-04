/**
 * Captures a protocol-specific UDP screenshot for the home-page
 * protocol-card popup. Drives the locally-running UDP sample via
 * Playwright, picks the Listener/monitor method on the multicast
 * URL, executes the stream, waits for ~10 datagrams, then shoots.
 *
 * Assumes Kuestenlogik.Bowire.Protocol.Udp.Sample is running on
 * http://localhost:5081 with udp://239.0.13.37:8137 + udp://127.0.0.1:8138
 * pre-seeded as ServerUrls. Set THEME=light for the light variant.
 *
 * Writes 1280×720 PNGs to site/assets/images/screenshots/streaming-udp-${theme}.png
 * (with a docs/ mirror so DocFX shares the same artefact).
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'screenshots');
const DOCS_OUT = path.resolve(__dirname, '..', 'docs', 'images', 'screenshots');
for (const dir of [OUT, DOCS_OUT]) if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

const URL = 'http://localhost:5081/bowire';
const THEME = (process.env.THEME || 'dark').toLowerCase();
const NAME = 'streaming-udp';

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        viewport: { width: 1280, height: 720 },
        deviceScaleFactor: 2,
        ignoreHTTPSErrors: true,
        colorScheme: THEME === 'light' ? 'light' : 'dark',
    });
    const page = await ctx.newPage();

    log(`navigating to ${URL} (theme=${THEME})`);
    await page.goto(URL, { waitUntil: 'networkidle' });
    await page.evaluate((theme) => {
        try { localStorage.setItem('bowire-theme', theme); } catch (_) {}
    }, THEME);
    await page.reload({ waitUntil: 'networkidle' });

    // Wait for the sidebar to populate from the multicast URL.
    await page.waitForSelector('.bowire-method-item', { timeout: 30000 });
    log('sidebar populated');

    // Click the streaming method.
    await page.locator('.bowire-method-item', { hasText: 'monitor' }).first().click();
    await page.waitForTimeout(400);

    await page.locator('.bowire-execute-btn, #bowire-execute-btn').first().click();
    log('stream started — waiting 5s for harbour datagrams');
    await page.waitForTimeout(5000);

    const file = path.join(OUT, `${NAME}-${THEME}.png`);
    await page.screenshot({ path: file, fullPage: false });
    fs.copyFileSync(file, path.join(DOCS_OUT, `${NAME}-${THEME}.png`));
    log(`  -> ${NAME}-${THEME}.png  (site + docs)`);

    const cancelBtn = page.locator('.bowire-cancel-btn, #bowire-cancel-btn').first();
    if (await cancelBtn.isVisible().catch(() => false)) await cancelBtn.click().catch(() => {});

    await browser.close();
})().catch(err => { console.error(err); process.exit(1); });
