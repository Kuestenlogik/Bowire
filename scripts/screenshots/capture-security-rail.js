/**
 * Captures the Security rail's main pane (OWASP API Top 10 suite #173 +
 * endpoint spider #176) with Playwright — a quick way to eyeball the
 * design-system styling of those sections without the Claude-for-Chrome
 * extension.
 *
 * Drives a running standalone Tool (no in-process services needed — the
 * OWASP catalog + spider are served by the Security.Scanner package). Start
 * one first, e.g.:
 *
 *     bowire            # serves http://localhost:5080/
 *
 * Then:
 *
 *     THEME=dark  node scripts/screenshots/capture-security-rail.js
 *     THEME=light BOWIRE_URL=http://localhost:5180/ node scripts/screenshots/capture-security-rail.js
 *
 * Writes security-rail-${theme}.png into docs/images/screenshots/.
 */
const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const URL = process.env.BOWIRE_URL || process.argv[2] || 'http://localhost:5080/';
const THEME = (process.env.THEME || 'dark').toLowerCase();
const OUT_DIR = path.resolve(__dirname, '..', '..', 'docs', 'images', 'screenshots');
const OUT = path.join(OUT_DIR, `security-rail-${THEME}.png`);

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

// v2.0 rail-mode catalogue: every mode-switch icon is a .bowire-rail-btn
// carrying data-rail-mode-id (mirrors capture-screenshots.js).
async function clickRail(page, modeId) {
    const sel = `.bowire-rail-btn[data-rail-mode-id="${modeId}"]`;
    const btn = page.locator(sel).first();
    await btn.waitFor({ state: 'visible', timeout: 10000 });
    await btn.click().catch(() => {});
    await page.waitForTimeout(400);
}

(async () => {
    if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, { recursive: true });
    const browser = await chromium.launch();
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 1200 } });
        log(`loading ${URL}`);
        await page.goto(URL, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(800);

        // Force the requested theme so dark/light variants both capture.
        await page.evaluate((t) => { document.documentElement.setAttribute('data-theme', t); }, THEME);

        log('switching to the Security rail');
        await clickRail(page, 'security');
        // Let the OWASP catalog fetch + the ten rows render.
        await page.waitForTimeout(2000);

        const main = page.locator('#bowire-main-security');
        if (await main.count()) {
            await main.screenshot({ path: OUT });
        } else {
            log('WARNING: #bowire-main-security not found — capturing the viewport instead');
            await page.screenshot({ path: OUT, fullPage: false });
        }
        log(`saved ${OUT}`);
    } finally {
        await browser.close();
    }
})().catch((e) => { console.error(e); process.exit(1); });
